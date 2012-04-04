using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace GitImporter
{
    public class ChangeSetBuilder
    {
        private class LabelInfo
        {
            public string Name { get; private set; }
            public List<ElementVersion> Versions { get; private set; }
            public HashSet<ElementVersion> MissingVersions { get; private set; }

            public LabelInfo(string name)
            {
                Name = name;
                Versions = new List<ElementVersion>();
            }

            public void Reset()
            {
                MissingVersions = new HashSet<ElementVersion>(Versions.Where(v => v.VersionNumber != 0));
            }
        }

        public static TraceSource Logger = Program.Logger;

        private const int MAX_DELAY = 20;
        private static readonly ChangeSet.Comparer _comparer = new ChangeSet.Comparer();

        private readonly Dictionary<string, Element> _elementsByOid;
        /// <summary>
        /// _roots are directory whose parents have not been requested :
        /// they will therefore never appear in the Content of a DirectoryVersion
        /// </summary>
        private readonly HashSet<string> _roots = new HashSet<string>();
        private List<Regex> _branchFilters;

        // ChangeSets, grouped first by branch, then by author
        private Dictionary<string, Dictionary<string, List<ChangeSet>>> _changeSets;
        private List<ChangeSet> _flattenChangeSets;
        /// <summary>
        /// For each branch, its parent branch
        /// </summary>
        private Dictionary<string, string> _globalBranches;
        private Dictionary<string, LabelInfo> _labels = new Dictionary<string,LabelInfo>();

        public ChangeSetBuilder(VobDB vobDB)
        {
            _elementsByOid = vobDB.ElementsByOid;
        }

        public void SetBranchFilters(string[] branches)
        {
            if (branches != null && branches.Length > 0)
                _branchFilters = branches.Select(b => new Regex(b)).ToList();
        }

        public void SetRoots(IEnumerable<string> roots)
        {
            _roots.Clear();
            foreach (var root in roots)
                _roots.Add(root);
        }

        public IList<ChangeSet> Build()
        {
            var allElementBranches = CreateRawChangeSets();
            ComputeGlobalBranches(allElementBranches);
            FilterBranches();
            FilterLabels();
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
                    Dictionary<string, List<ChangeSet>> branchChangeSets;
                    if (!_changeSets.TryGetValue(branch.BranchName, out branchChangeSets))
                    {
                        branchChangeSets = new Dictionary<string, List<ChangeSet>>();
                        _changeSets.Add(branch.BranchName, branchChangeSets);
                    }
                    foreach (var version in branch.Versions)
                    {
                        ElementVersion versionForLabel = version.VersionNumber == 0 && version.Branch.BranchingPoint != null
                            ? version.Branch.BranchingPoint : version;
                        foreach (var label in version.Labels)
                        {
                            LabelInfo labelInfo;
                            if (!_labels.TryGetValue(label, out labelInfo))
                            {
                                labelInfo = new LabelInfo(label);
                                _labels.Add(label, labelInfo);
                            }
                            labelInfo.Versions.Add(versionForLabel);
                        }
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
            if (_branchFilters == null || _branchFilters.Count == 0)
                return;
            var branchesToRemove = new HashSet<string>(_globalBranches.Keys.Where(b => !_branchFilters.Exists(r => r.IsMatch(b))));
            bool finished = false;
            while (!finished)
            {
                finished = true;
                foreach (var toRemove in branchesToRemove)
                {
                    // only branches from which no non-filtered branches spawn can be removed
                    if (_globalBranches.ContainsKey(toRemove) && !_globalBranches.Values.Contains(toRemove))
                    {
                        Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet, "Branch " + toRemove + " filtered out");
                        finished = false;
                        _globalBranches.Remove(toRemove);
                        _changeSets.Remove(toRemove);
                    }
                }
            }
        }

        private void FilterLabels()
        {
            var labelsToRemove = _labels.Values
                .Where(l => l.Versions.Exists(v => !_globalBranches.ContainsKey(v.Branch.BranchName)))
                .Select(l => l.Name).ToList();
            foreach (var toRemove in labelsToRemove)
            {
                Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet, "Label " + toRemove + " filtered : was on a filtered out branch");
                _labels.Remove(toRemove);
            }
            foreach (var label in _labels.Values)
                label.Reset();
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
            // some moves (rename) may be in separate ChangeSets, we must be able to know what version to write at the new location
            var elementVersionsByBranch = new Dictionary<string, Dictionary<Element, ElementVersion>>();
            foreach (var changeSet in _flattenChangeSets)
            {
                n++;
                changeSet.Id = n;
                branchTips[changeSet.Branch] = changeSet;

                Dictionary<Element, string> elementNames;
                Dictionary<Element, ElementVersion> elementVersions;
                bool isNewBranch = !startedBranches.Contains(changeSet.Branch);
                if (isNewBranch)
                {
                    // n is 1-based index because it is used as a mark id for git fast-import, that reserves id 0
                    if (changeSet.Branch != "main")
                    {
                        string parentBranch = _globalBranches[changeSet.Branch];
                        changeSet.BranchingPoint = branchTips[parentBranch];
                        elementNames = new Dictionary<Element, string>(elementNamesByBranch[parentBranch]);
                        elementVersions = new Dictionary<Element, ElementVersion>(elementVersionsByBranch[parentBranch]);
                    }
                    else
                    {
                        elementNames = new Dictionary<Element, string>();
                        elementVersions = new Dictionary<Element, ElementVersion>();
                    }
                    elementNamesByBranch.Add(changeSet.Branch, elementNames);
                    elementVersionsByBranch.Add(changeSet.Branch, elementVersions);
                    startedBranches.Add(changeSet.Branch);
                }
                else
                {
                    elementNames = elementNamesByBranch[changeSet.Branch];
                    elementVersions = elementVersionsByBranch[changeSet.Branch];
                }

                // first update current version of changed elements
                foreach (var namedVersion in changeSet.Versions)
                    elementVersions[namedVersion.Version.Element] = namedVersion.Version;
                
                ProcessDirectoryChanges(changeSet, elementNames, elementVersions, orphanedVersionsByElement);
                
                foreach (var namedVersion in changeSet.Versions)
                {
                    changeSet.Labels.AddRange(ProcessLabels(namedVersion.Version, elementVersions));
                    string elementName;
                    if (!elementNames.TryGetValue(namedVersion.Version.Element, out elementName))
                    {
                        if (namedVersion.Name != null)
                            throw new Exception("Version " + namedVersion.Version + " was named " + namedVersion.Name + ", but had no entry in elementNames");

                        Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet,
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
            // really lost versions
            foreach (var orphanedVersions in orphanedVersionsByElement.Values)
                foreach (var orphanedVersion in orphanedVersions)
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                     "Version " + orphanedVersion.Item2.Version + " has not been visible in any imported directory version");
        }

        private void ProcessDirectoryChanges(ChangeSet changeSet, Dictionary<Element, string> elementNames, Dictionary<Element, ElementVersion> elementVersions,
            Dictionary<Element, List<Tuple<string, ChangeSet.NamedVersion>>> orphanedVersionsByElement)
        {
            // first order from roots to leaves (because changes to roots also impact leaves)
            var unorderedVersions = new List<DirectoryVersion>(changeSet.Versions.Select(v => v.Version).OfType<DirectoryVersion>());
            var orderedVersions = new List<DirectoryVersion>();
            while (unorderedVersions.Count > 0)
            {
                var notReferenced = unorderedVersions.FindAll(v => !unorderedVersions.Exists(parent => parent.Content.Exists(pair => pair.Value == v.Element)));
                if (notReferenced.Count == 0)
                    throw new Exception("Circular references in directory versions of a change set");
                foreach (var v in notReferenced)
                    unorderedVersions.Remove(v);
                orderedVersions.AddRange(notReferenced);
            }

            // loop for deletes and moves, using "old" elementNames
            var removedElements = new Dictionary<Element, string>();
            // so too early to know the new complete names of added elements : we must keep their parent
            var addedElements = new Dictionary<Element, Tuple<Element, string>>();
            foreach (var version in orderedVersions)
            {
                if (version.VersionNumber == 0)
                    continue;

                string baseName;
                if (!elementNames.TryGetValue(version.Element, out baseName) && _roots.Contains(version.Element.Name))
                {
                    baseName = version.Element.Name.Replace('\\', '/');
                    elementNames.Add(version.Element, baseName);
                }
                if (baseName != null)
                    baseName += "/";
                ComputeDiffWithPrevious(version, baseName, removedElements, addedElements);
            }

            ProcessRemove(changeSet, elementNames, removedElements, addedElements);

            // then update elementNames so that later changes of the elements will be at correct location
            foreach (var version in orderedVersions)
            {
                string elementName;
                if (!elementNames.TryGetValue(version.Element, out elementName))
                    // removed by one of the changes
                    continue;
                string baseName = elementName + "/";
                UpdateChildNames(version, baseName, elementNames, elementVersions);
            }

            ProcessRename(changeSet, elementNames, removedElements, addedElements);

            // now remaining added elements
            foreach (var pair in addedElements)
            {
                string baseName;
                if (elementNames.TryGetValue(pair.Value.Item1, out baseName))
                    baseName += "/";
                AddElement(changeSet, pair.Key, baseName, pair.Value.Item2, elementNames, elementVersions, orphanedVersionsByElement);
            }
        }

        private static void UpdateChildNames(DirectoryVersion version, string baseName,
            Dictionary<Element, string> elementNames, Dictionary<Element, ElementVersion> elementVersions)
        {
            ElementVersion childVersion;
            foreach (var child in version.Content)
            {
                elementNames[child.Value] = baseName + child.Key;
                if (child.Value.IsDirectory && elementVersions.TryGetValue(child.Value, out childVersion))
                    UpdateChildNames((DirectoryVersion)childVersion, baseName + child.Key + "/", elementNames, elementVersions);
            }
        }

        private static void ComputeDiffWithPrevious(DirectoryVersion version, string baseName, Dictionary<Element, string> removedElements, Dictionary<Element, Tuple<Element, string>> addedElements)
        {
            var previousVersion = (DirectoryVersion)version.Branch.Versions[version.Branch.Versions.IndexOf(version) - 1];
            foreach (var pair in previousVersion.Content)
            {
                var inNew = version.Content.FirstOrDefault(p => p.Value == pair.Value);
                // if inNew is the "Default", inNew.Value == null
                if (inNew.Value != null && inNew.Key == pair.Key)
                    // unchanged
                    continue;
                // we may have several versions of the same directory in a ChangeSet : keep first Remove, last Add
                // if baseName is null, it means this version is not visible (yet) : nothing to actually remove
                if (baseName != null && !removedElements.ContainsKey(pair.Value))
                    removedElements.Add(pair.Value, baseName + pair.Key);
                if (inNew.Value != null)
                    addedElements[pair.Value]= new Tuple<Element, string>(version.Element, inNew.Key);
            }
            foreach (var pair in version.Content)
            {
                if (!previousVersion.Content.Exists(p => p.Value == pair.Value))
                    addedElements.Add(pair.Value, new Tuple<Element, string>(version.Element, pair.Key));
            }
        }

        private static void ProcessRemove(ChangeSet changeSet, Dictionary<Element, string> elementNames,
            Dictionary<Element, string> removedElements, Dictionary<Element, Tuple<Element, string>> addedElements)
        {
            // handles remove (directory rename may impact other changes)
            // iterate on a copy so that we can remove handled entries
            foreach (var pair in removedElements.ToList())
            {
                if (!addedElements.ContainsKey(pair.Key))
                {
                    bool removedWithParent = false;
                    foreach (var removed in changeSet.Removed)
                        if (pair.Value.StartsWith(removed + "/"))
                        {
                            removedWithParent = true;
                            break;
                        }
                    if (!removedWithParent)
                        changeSet.Removed.Add(pair.Value);
                    removedElements.Remove(pair.Key);
                    // not available anymore
                    elementNames.Remove(pair.Key);
                    // TODO : should we remove child elementNames ? probably.
                }
            }
        }

        private static void ProcessRename(ChangeSet changeSet, Dictionary<Element, string> elementNames,
            Dictionary<Element, string> removedElements, Dictionary<Element, Tuple<Element, string>> addedElements)
        {
            // now elementNames have target names
            // we know that entries in removedElements are ordered from root to leaf
            // we still need to update the old name if a parent directory has already been moved
            foreach (var pair in removedElements)
            {
                var oldName = pair.Value;
                foreach (var rename in changeSet.Renamed)
                {
                    // changeSet.Renamed is in correct order
                    if (oldName.StartsWith(rename.Item1 + "/"))
                        oldName = oldName.Replace(rename.Item1 + "/", rename.Item2 + "/");
                }
                var newName = addedElements[pair.Key];
                string elementName;
                if (elementNames.TryGetValue(newName.Item1, out elementName))
                {
                    changeSet.Renamed.Add(new Tuple<string, string>(oldName, elementName + "/" + newName.Item2));
                    addedElements.Remove(pair.Key);
                }
                // else destination not visible yet : another (hopefully temporary) orphan
            }
        }

        private static void AddElement(ChangeSet changeSet, Element element, string baseName, string name,
            Dictionary<Element, string> elementNames, Dictionary<Element, ElementVersion> elementVersions,
            Dictionary<Element, List<Tuple<string, ChangeSet.NamedVersion>>> orphanedVersionsByElement)
        {
            ElementVersion currentVersion;
            if (!elementVersions.TryGetValue(element, out currentVersion))
                // assumed to be (empty) version 0
                return;

            if (element.IsDirectory)
            {
                foreach (var subElement in ((DirectoryVersion)currentVersion).Content)
                    AddElement(changeSet, subElement.Value, baseName == null ? null : baseName + name + "/", subElement.Key, elementNames, elementVersions, orphanedVersionsByElement);
                return;
            }
            List<ChangeSet.NamedVersion> existing = changeSet.Versions.Where(v => v.Version.Element == element).ToList();
            ChangeSet.NamedVersion addedNamedVersion = null;
            if (existing.Count > 1)
                throw new Exception("Unexpected number of versions (" + existing.Count + ") of file element " + element + " in change set " + changeSet);

            string fullName = baseName == null ? null : baseName + name;
            if (existing.Count == 1)
            {
                if (existing[0].Version != currentVersion)
                    throw new Exception("Unexpected mismatch of versions of file element " + element + " in change set " + changeSet + " : " + existing[0].Version + " != " + currentVersion);
                if (existing[0].Name != null && fullName != null && existing[0].Name != fullName)
                    // TODO : maybe this could be normal with links... but not compatible with elementNames as it is
                    throw new Exception("Unexpected mismatch of names of file element " + element + " in change set " + changeSet + " : " + existing[0].Name + " != " + fullName);
                existing[0].Name = fullName;
            }
            else
                addedNamedVersion = changeSet.Add(currentVersion, fullName, true);

            List<Tuple<string, ChangeSet.NamedVersion>> orphanedVersions;
            if ((!orphanedVersionsByElement.TryGetValue(element, out orphanedVersions) || orphanedVersions.Count == 0) && (addedNamedVersion == null || fullName != null))
                // nothing to patch, and no new orphan : done
                return;

            if (addedNamedVersion != null && fullName == null)
            {
                // sadly another orphan
                if (orphanedVersions == null)
                {
                    orphanedVersions = new List<Tuple<string, ChangeSet.NamedVersion>>();
                    orphanedVersionsByElement.Add(element, orphanedVersions);
                }
                orphanedVersions.Add(new Tuple<string, ChangeSet.NamedVersion>(changeSet.Branch, addedNamedVersion));
                return;
            }

            // we've got a name here, maybe we can patch some orphans
            var completed = new List<Tuple<string, ChangeSet.NamedVersion>>();
            foreach (var namedVersion in orphanedVersions)
                if (namedVersion.Item1 == changeSet.Branch && namedVersion.Item2.Version == currentVersion)
                {
                    namedVersion.Item2.Name = fullName;
                    completed.Add(namedVersion);
                }
            foreach (var toRemove in completed)
                orphanedVersions.Remove(toRemove);
            if (orphanedVersions.Count == 0)
                orphanedVersionsByElement.Remove(element);
        }

        private IEnumerable<string> ProcessLabels(ElementVersion version, Dictionary<Element, ElementVersion> elementVersions)
        {
            var result = new List<string>();
            foreach (var label in version.Labels)
            {
                LabelInfo labelInfo;
                if (!_labels.TryGetValue(label, out labelInfo))
                    continue;
                labelInfo.MissingVersions.Remove(version);
                if (labelInfo.MissingVersions.Count > 0)
                    continue;
                // so we removed the last missing version, check that everything is still OK
                _labels.Remove(label);
                bool ok = true;
                foreach (var toCheck in labelInfo.Versions)
                {
                    if ((toCheck.VersionNumber == 0 && elementVersions.ContainsKey(toCheck.Element)) ||
                        (toCheck.VersionNumber != 0 && elementVersions[toCheck.Element] != toCheck))
                    {
                        Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet,
                            "Label " + label + " is inconsistent : should be on " + toCheck + ", not on " + elementVersions[toCheck.Element]);
                        ok = false;
                    }
                }
                if (!ok)
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                        "Label " + label + " was inconsistent : not applied");
                else
                    result.Add(label);
            }
            return result;
        }
    }
}
