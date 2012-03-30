using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace GitImporter
{
    public class ChangeSetBuilder
    {
        public static TraceSource Logger = Program.Logger;

        private const int MAX_DELAY = 20;
        private static readonly ChangeSet.Comparer _comparer = new ChangeSet.Comparer();

        private readonly Dictionary<string, Element> _elementsByOid;

        private List<Regex> _branchFilters;
        private Dictionary<string, Dictionary<string, List<ChangeSet>>> _changeSets;
        private List<ChangeSet> _flattenChangeSets;
        /// <summary>
        /// For each branch, its parent branch
        /// </summary>
        private Dictionary<string, string> _globalBranches;

        public ChangeSetBuilder(VobDB vobDB)
        {
            _elementsByOid = vobDB.ElementsByOid;
        }

        public void SetBranchFilters(string[] branches)
        {
            if (branches != null && branches.Length > 0)
                _branchFilters = branches.Select(b => new Regex(b)).ToList();
        }

        public IList<ChangeSet> Build()
        {
            var allElementBranches = CreateRawChangeSets();
            ComputeGlobalBranches(allElementBranches);
            ProcessElementNames();
            return _flattenChangeSets;
        }

        private IEnumerable<string> CreateRawChangeSets()
        {
            // the list must always be kept sorted, so that BinarySearch works
            // if the size of the list gets too big and (mostly) linear insertion time becomes a problem,
            // we could look at SorteList<> (which is not actually a list, but a dictionary)
            _changeSets = new Dictionary<string, Dictionary<string, List<ChangeSet>>>();
            // keep all FullName's, so that we can try to guess "global" BranchingPoint
            var allElementBranches = new HashSet<string>();
            foreach (var element in _elementsByOid.Values)
                foreach (var branch in element.Branches.Values)
                {
                    allElementBranches.Add(branch.FullName);
                    if (branch.BranchName != "main" && _branchFilters != null && !_branchFilters.Any(e => e.IsMatch(branch.BranchName)))
                        continue;
                    Dictionary<string, List<ChangeSet>> branchChangeSets;
                    if (!_changeSets.TryGetValue(branch.BranchName, out branchChangeSets))
                    {
                        branchChangeSets = new Dictionary<string, List<ChangeSet>>();
                        _changeSets.Add(branch.BranchName, branchChangeSets);
                    }
                    foreach (var version in branch.Versions)
                    {
                        if (version.VersionNumber == 0)
                            continue;
                        List<ChangeSet> authorChangeSets;
                        if (!branchChangeSets.TryGetValue(version.AuthorLogin, out authorChangeSets))
                        {
                            authorChangeSets = new List<ChangeSet>();
                            branchChangeSets.Add(version.AuthorLogin, authorChangeSets);
                        }
                        AddVersion(authorChangeSets, version);
                    }
                }

            return allElementBranches;
        }

        private static void AddVersion(List<ChangeSet> changeSets, ElementVersion version)
        {
            // used either for search or for new ChangeSet
            var changeSet = new ChangeSet(version.AuthorName, version.AuthorLogin, version.Branch.BranchName, version.Date);
            if (changeSets.Count == 0)
            {
                changeSet.Add(version);
                changeSets.Add(changeSet);
                return;
            }

            int index = changeSets.BinarySearch(changeSet, _comparer);
            if (index >= 0)
            {
                changeSets[index].Add(version);
                return;
            }

            index = ~index; // index of first element bigger
            if (index == changeSets.Count)
            {
                // so even the last one is not bigger
                ChangeSet candidate = changeSets[index - 1];
                if (version.Date <= candidate.FinishTime.AddSeconds(MAX_DELAY))
                    candidate.Add(version);
                else
                {
                    changeSet.Add(version);
                    changeSets.Add(changeSet);
                }
                return;
            }
            if (index == 0)
            {
                ChangeSet candidate = changeSets[0];
                if (version.Date >= candidate.StartTime.AddSeconds(-MAX_DELAY))
                    candidate.Add(version);
                else
                {
                    changeSet.Add(version);
                    changeSets.Insert(0, changeSet);
                }
                return;
            }
            DateTime lowerBound = changeSets[index - 1].FinishTime;
            DateTime upperBound = changeSets[index].StartTime;
            if (version.Date <= lowerBound.AddSeconds(MAX_DELAY) && version.Date < upperBound.AddSeconds(-MAX_DELAY))
            {
                changeSets[index - 1].Add(version);
                return;
            }
            if (version.Date > lowerBound.AddSeconds(MAX_DELAY) && version.Date >= upperBound.AddSeconds(-MAX_DELAY))
            {
                changeSets[index].Add(version);
                return;
            }
            if (version.Date > lowerBound.AddSeconds(MAX_DELAY) && version.Date < upperBound.AddSeconds(-MAX_DELAY))
            {
                changeSet.Add(version);
                changeSets.Insert(index, changeSet);
                return;
            }
            // last case : we should merge the two ChangeSets (that are now "linked" by the version we are adding)
            changeSets[index - 1].Add(version);
            foreach (var v in changeSets[index].Versions)
                changeSets[index - 1].Add(v.Version);
            changeSets.RemoveAt(index);
        }

        private void ComputeGlobalBranches(IEnumerable<string> allElementBranches)
        {
            var allPotentialParents = new Dictionary<string, HashSet<string>>();
            foreach (var branch in allElementBranches)
            {
                var path = branch.Split('\\');
                if (path.Length <= 1)
                    continue;
                HashSet<string> potentialParents;
                string branchName = path[path.Length - 1];
                if (!allPotentialParents.TryGetValue(branchName, out potentialParents))
                {
                    potentialParents = new HashSet<string>();
                    allPotentialParents.Add(branchName, potentialParents);
                }
                potentialParents.Add(path[path.Length - 2]);
            }
            var depths = allPotentialParents.Keys.ToDictionary(s => s, unused => 0);
            depths["main"] = 1;
            bool finished = false;
            while (!finished)
            {
                finished = true;
                foreach (var pair in allPotentialParents)
                {
                    int depth = pair.Value.Max(p => depths[p]) + 1;
                    if (depth > depths[pair.Key])
                    {
                        finished = false;
                        depths[pair.Key] = depth;
                    }
                }
            }
            _globalBranches = new Dictionary<string, string>();
            _globalBranches["main"] = null;
            foreach (var pair in allPotentialParents)
            {
                var maxDepth = pair.Value.Max(p => depths[p]);
                var candidates = pair.Value.Where(p => depths[p] == maxDepth);
                if (candidates.Count() != 1)
                    throw new Exception("Could not compute parent of branch " + pair.Key + " among " + string.Join(", ", candidates));
                _globalBranches[pair.Key] = candidates.First();
            }
        }

        private void FilterBranches()
        {
            // TODO : only branches from which no non-filtered branches spawn can be removed
            if (_branchFilters == null || _branchFilters.Count == 0)
                return;
            var branchesToRemove = new HashSet<string>(_globalBranches.Keys.Where(b => !_branchFilters.Exists(r => r.IsMatch(b))));
            bool finished = false;
            while (!finished)
            {
                finished = true;
                var newGlobalBranches = new Dictionary<string, string>(_globalBranches);
                foreach (var pair in _globalBranches)
                {
                    if (branchesToRemove.Contains(pair.Value))
                    {
                        finished = false;
                        newGlobalBranches[pair.Key] = _globalBranches[pair.Value];
                    }
                }
                _globalBranches = newGlobalBranches;
            }
        }

        private void ProcessElementNames()
        {
            _flattenChangeSets = _changeSets.Values.SelectMany(d => d.Values.SelectMany(l => l)).OrderBy(c => c, _comparer).ToList();
            int n = 0;

            var startedBranches = new HashSet<string>();
            var branchTips = new Dictionary<string, ChangeSet>();
            var elementNamesByBranch = new Dictionary<string, Dictionary<Element, string>>();
            // branch and version for which the elementName could not be found
            var orphanedVersionsByElement = new Dictionary<Element, List<Tuple<string, ChangeSet.NamedVersion>>>();
            foreach (var changeSet in _flattenChangeSets)
            {
                n++;
                changeSet.Id = n;
                branchTips[changeSet.Branch] = changeSet;

                Dictionary<Element, string> elementNames;
                bool isNewBranch = !startedBranches.Contains(changeSet.Branch);
                if (isNewBranch)
                {
                    // n is 1-based index because it is used as a mark id for git fast-import, that reserves id 0
                    if (changeSet.Branch != "main")
                    {
                        string parentBranch = _globalBranches[changeSet.Branch];
                        changeSet.BranchingPoint = branchTips[parentBranch];
                        elementNames = new Dictionary<Element, string>(elementNamesByBranch[parentBranch]);
                    }
                    else
                        elementNames = new Dictionary<Element, string>();
                    elementNamesByBranch.Add(changeSet.Branch, elementNames);
                    startedBranches.Add(changeSet.Branch);
                }
                else
                    elementNames = elementNamesByBranch[changeSet.Branch];
                
                ProcessDirectoryChanges(changeSet, elementNames, orphanedVersionsByElement);
                
                foreach (var namedVersion in changeSet.Versions)
                {
                    string elementName;
                    if (!elementNames.TryGetValue(namedVersion.Version.Element, out elementName))
                    {
                        Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                         "Version " + namedVersion.Version + " was not yet visible in an existing directory version");
                        List<Tuple<string, ChangeSet.NamedVersion>> orphanedVersions;
                        if (!orphanedVersionsByElement.TryGetValue(namedVersion.Version.Element, out orphanedVersions))
                        {
                            orphanedVersions = new List<Tuple<string, ChangeSet.NamedVersion>>();
                            orphanedVersionsByElement.Add(namedVersion.Version.Element, orphanedVersions);
                        }
                        orphanedVersions.Add(new Tuple<string, ChangeSet.NamedVersion>(changeSet.Branch, namedVersion));
                        continue;
                    }
                    namedVersion.Name = elementName;
                }
            }
            foreach (var orphanedVersions in orphanedVersionsByElement.Values)
                foreach (var orphanedVersion in orphanedVersions)
                {
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                     "Version " + orphanedVersion.Item2.Version + " has not been visible in any imported directory version");
                }
        }

        private static void ProcessDirectoryChanges(ChangeSet changeSet, Dictionary<Element, string> elementNames,
            Dictionary<Element, List<Tuple<string, ChangeSet.NamedVersion>>> orphanedVersionsByElement)
        {
            // TODO : if a directory move is not in a sigle ChangeSet, the add must recreate the complete content

            // first order from roots to leaves (because changes to roots also impact leaves)
            var unorderedVersions = new List<DirectoryVersion>(changeSet.Versions.Select(v => v.Version).OfType<DirectoryVersion>());
            var orderedVersions = new List<DirectoryVersion>();
            while (unorderedVersions.Count > 0)
            {
                var notReferenced = unorderedVersions.FindAll(v => !unorderedVersions.Exists(parent => parent.Content.Exists(pair => pair.Value.Oid == v.Element.Oid)));
                if (notReferenced.Count == 0)
                    throw new Exception("Circular references in directory versions of a change set");
                foreach (var v in notReferenced)
                    unorderedVersions.Remove(v);
                orderedVersions.AddRange(notReferenced);
            }

            // first loop for deletes and moves, using "old" elementNames
            var removedElements = new Dictionary<Element, string>();
            var addedElements = new Dictionary<Element, string>();
            foreach (var version in orderedVersions)
            {
                if (version.VersionNumber == 0)
                    continue;
                // TODO : check handling of root directory
                string baseName;
                if (!elementNames.TryGetValue(version.Element, out baseName))
                {
                    baseName = version.Element.Name.Replace('\\', '/');
                    elementNames.Add(version.Element, baseName);
                }
                baseName += "/";
                ComputeDiffWithPrevious(version, baseName, removedElements, addedElements);
            }

            foreach (var pair in removedElements)
            {
                string newName;
                if (addedElements.TryGetValue(pair.Key, out newName))
                    changeSet.Renamed.Add(new Tuple<string, string>(pair.Value, newName));
                else
                    changeSet.Removed.Add(pair.Value);
            }
            foreach (var pair in addedElements)
            {
                List<Tuple<string, ChangeSet.NamedVersion>> orphanedVersions;
                if (!orphanedVersionsByElement.TryGetValue(pair.Key, out orphanedVersions) || orphanedVersions.Count == 0)
                    continue;
                var completed = new List<Tuple<string, ChangeSet.NamedVersion>>();
                foreach (var namedVersion in orphanedVersions)
                    if (namedVersion.Item1 == changeSet.Branch)
                    {
                        namedVersion.Item2.Name = pair.Value;
                        completed.Add(namedVersion);
                    }
                foreach (var toRemove in completed)
                    orphanedVersions.Remove(toRemove);
            }

            // then update elementNames so that later changes of the elements will be at correct location
            foreach (var version in orderedVersions)
            {
                string baseName = elementNames[version.Element] + "/";
                foreach (var child in version.Content)
                    elementNames[child.Value] = baseName + child.Key;
            }
        }

        private static void ComputeDiffWithPrevious(DirectoryVersion version, string baseName, Dictionary<Element, string> removedElements, Dictionary<Element, string> addedElements)
        {
            var previousVersion = (DirectoryVersion)version.Branch.Versions[version.Branch.Versions.IndexOf(version) - 1];
            foreach (var pair in previousVersion.Content)
            {
                var inNew = version.Content.FirstOrDefault(p => p.Value == pair.Value);
                // if inNew is the "Default", inNew.Value == null
                if (inNew.Value != null && inNew.Key == pair.Key)
                    // unchanged
                    continue;
                removedElements.Add(pair.Value, baseName + pair.Key);
                if (inNew.Value != null)
                    addedElements.Add(pair.Value, baseName + inNew.Key);
            }
            foreach (var pair in version.Content)
            {
                if (!previousVersion.Content.Exists(p => p.Value == pair.Value))
                    addedElements.Add(pair.Value, baseName + pair.Key);
            }
        }
    }
}
