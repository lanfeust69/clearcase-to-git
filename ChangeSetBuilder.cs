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
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Start creating raw ChangeSets");
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
                        if (version.VersionNumber == 0 && (version.Element.IsDirectory || version.Branch.BranchName != "main"))
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

            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Stop creating raw ChangeSets");
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

                allPotentialParents.AddToCollection(path[path.Length - 1], path[path.Length - 2]);
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
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Start process element names");
            _flattenChangeSets = _changeSets.Values.SelectMany(d => d.Values.SelectMany(l => l)).OrderBy(c => c, _comparer).ToList();
            int n = 0;

            var startedBranches = new HashSet<string>();
            var branchTips = new Dictionary<string, ChangeSet>();
            // an element may appear under different names, especially during a move,
            // if the destination directory has been checked in before source directory
            var elementsNamesByBranch = new Dictionary<string, Dictionary<Element, HashSet<string>>>();
            // branch and version for which the elementName could not be found
            var orphanedVersionsByElement = new Dictionary<Element, List<Tuple<string, ChangeSet.NamedVersion>>>();
            // some moves (rename) may be in separate ChangeSets, we must be able to know what version to write at the new location
            var elementsVersionsByBranch = new Dictionary<string, Dictionary<Element, ElementVersion>>();
            foreach (var changeSet in _flattenChangeSets)
            {
                n++;
                changeSet.Id = n;
                Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Start process element names in ChangeSet", changeSet);
                if (n % 1000 == 0)
                    Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet, "Processing element names in ChangeSet", changeSet);

                branchTips[changeSet.Branch] = changeSet;
                Dictionary<Element, HashSet<string>> elementsNames;
                Dictionary<Element, ElementVersion> elementsVersions;
                bool isNewBranch = !startedBranches.Contains(changeSet.Branch);
                if (isNewBranch)
                {
                    // n is 1-based index because it is used as a mark id for git fast-import, that reserves id 0
                    if (changeSet.Branch != "main")
                    {
                        string parentBranch = _globalBranches[changeSet.Branch];
                        changeSet.BranchingPoint = branchTips[parentBranch];
                        branchTips[parentBranch].IsBranchingPoint = true;
                        elementsNames = new Dictionary<Element, HashSet<string>>(elementsNamesByBranch[parentBranch]);
                        elementsVersions = new Dictionary<Element, ElementVersion>(elementsVersionsByBranch[parentBranch]);
                    }
                    else
                    {
                        elementsNames = new Dictionary<Element, HashSet<string>>();
                        elementsVersions = new Dictionary<Element, ElementVersion>();
                    }
                    elementsNamesByBranch.Add(changeSet.Branch, elementsNames);
                    elementsVersionsByBranch.Add(changeSet.Branch, elementsVersions);
                    startedBranches.Add(changeSet.Branch);
                }
                else
                {
                    elementsNames = elementsNamesByBranch[changeSet.Branch];
                    elementsVersions = elementsVersionsByBranch[changeSet.Branch];
                }

                // first update current version of changed elements, but keep old versions to handle remove/rename
                var oldVersions = new Dictionary<Element, ElementVersion>();
                foreach (var namedVersion in changeSet.Versions)
                {
                    ElementVersion oldVersion;
                    elementsVersions.TryGetValue(namedVersion.Version.Element, out oldVersion);
                    // we keep track that there was no previous version : null
                    if (!oldVersions.ContainsKey(namedVersion.Version.Element))
                        oldVersions.Add(namedVersion.Version.Element, oldVersion);
                    elementsVersions[namedVersion.Version.Element] = namedVersion.Version;
                }

                ProcessDirectoryChanges(changeSet, elementsNames, elementsVersions, oldVersions, orphanedVersionsByElement);

                foreach (var namedVersion in changeSet.Versions)
                {
                    changeSet.Labels.AddRange(ProcessLabels(namedVersion.Version, elementsVersions));

                    if (namedVersion.Names.Count > 0)
                        continue;

                    HashSet<string> elementNames;
                    if (!elementsNames.TryGetValue(namedVersion.Version.Element, out elementNames))
                    {
                        if (namedVersion.Names.Count > 0)
                            throw new Exception("Version " + namedVersion.Version + " was named " + namedVersion.Names[0] + ", but had no entry in elementNames");

                        Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet,
                            "Version " + namedVersion.Version + " was not yet visible in an existing directory version");
                        orphanedVersionsByElement.AddToCollection(namedVersion.Version.Element,
                            new Tuple<string, ChangeSet.NamedVersion>(changeSet.Branch, namedVersion));
                        continue;
                    }
                    namedVersion.Names.AddRange(elementNames);
                }
                Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Stop process element names in ChangeSet", changeSet.Id);
            }

            // really lost versions
            foreach (var orphanedVersions in orphanedVersionsByElement.Values)
                foreach (var orphanedVersion in orphanedVersions)
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                     "Version " + orphanedVersion.Item2.Version + " has not been visible in any imported directory version");

            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Stop process element names");
        }

        private void ProcessDirectoryChanges(ChangeSet changeSet, Dictionary<Element, HashSet<string>> elementsNames,
            Dictionary<Element, ElementVersion> elementsVersions, Dictionary<Element, ElementVersion> oldVersions,
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

            // we need to keep what we put in removedElements and addedElements in order (same reason as orderedVersions)
            // we may want to switch to (unfortunately not generic) OrderedDictionary if perf becomes an issue
            var removedElements = new List<KeyValuePair<Element, List<Tuple<Element, string>>>>();
            var addedElements = new List<KeyValuePair<Element, List<Tuple<Element, string>>>>();
            foreach (var version in orderedVersions)
            {
                if (version.VersionNumber == 0)
                    continue;
                ComputeDiffWithPrevious(version, removedElements, addedElements);
            }

            var renamedElements = ProcessRemove(changeSet, elementsNames, elementsVersions, oldVersions, removedElements, addedElements);

            // then update elementNames so that later changes of the elements will be at correct location
            foreach (var version in orderedVersions)
            {
                // here, we want to process only the most recent version (if there was several)
                if (orderedVersions.Any(v => v.Element == version.Element && v.VersionNumber > version.VersionNumber))
                    continue;
                HashSet<string> elementNames;
                if (!elementsNames.TryGetValue(version.Element, out elementNames))
                {
                    if (_roots.Contains(version.Element.Name))
                    {
                        elementNames = new HashSet<string> { version.Element.Name.Replace('\\', '/') };
                        elementsNames.Add(version.Element, elementNames);
                    }
                    else
                        // removed by one of the changes
                        continue;
                }
                foreach (string baseName in elementNames)
                    UpdateChildNames(version, baseName + "/", elementsNames, elementsVersions);
            }

            ProcessRename(changeSet, elementsNames, elementsVersions, oldVersions, renamedElements, addedElements);

            // now remaining added elements
            foreach (var pair in addedElements)
                foreach (var namedInElement in pair.Value)
                {
                    HashSet<string> baseNames;
                    if (elementsNames.TryGetValue(namedInElement.Item1, out baseNames))
                        baseNames = new HashSet<string>(baseNames.Select(s => s + "/"));
                    else
                        baseNames = new HashSet<string> { null };
                    foreach (string baseName in baseNames)
                        AddElement(changeSet, pair.Key, baseName, namedInElement.Item2, elementsVersions, orphanedVersionsByElement);
                }
        }

        private static void UpdateChildNames(DirectoryVersion version, string baseName,
            Dictionary<Element, HashSet<string>> elementsNames, Dictionary<Element, ElementVersion> elementsVersions)
        {
            ElementVersion childVersion;
            foreach (var child in version.Content)
            {
                elementsNames.AddToCollection(child.Value, baseName + child.Key);
                if (child.Value.IsDirectory && elementsVersions.TryGetValue(child.Value, out childVersion))
                    UpdateChildNames((DirectoryVersion)childVersion, baseName + child.Key + "/", elementsNames, elementsVersions);
            }
        }

        private static void ComputeDiffWithPrevious(DirectoryVersion version,
            List<KeyValuePair<Element, List<Tuple<Element, string>>>> removedElements,
            List<KeyValuePair<Element, List<Tuple<Element, string>>>> addedElements)
        {
            // we never put (uninteresting) version 0 in a changeSet, but it is still in the ElementBranch.Versions
            var previousVersion = (DirectoryVersion)version.Branch.Versions[version.Branch.Versions.IndexOf(version) - 1];
            foreach (var pair in previousVersion.Content)
            {
                Element childElement = pair.Value;
                // an element may appear under different names
                // KeyValuePair.Equals seems to be slow
                if (version.Content.Any(p => p.Key == pair.Key && p.Value == pair.Value))
                    continue;

                var namedInElement = new Tuple<Element, string>(version.Element, pair.Key);
                if (!addedElements.RemoveFromCollection(childElement, namedInElement))
                    removedElements.AddToCollection(childElement, namedInElement);
            }
            foreach (var pair in version.Content)
            {
                if (!previousVersion.Content.Any(p => p.Key == pair.Key && p.Value == pair.Value))
                    addedElements.AddToCollection(pair.Value, new Tuple<Element, string>(version.Element, pair.Key));
            }
        }

        /// <summary>
        /// handles simple removes (directory rename may impact other changes),
        /// and returns resolved old names for later renames
        /// </summary>
        private static List<KeyValuePair<Element, string>> ProcessRemove(ChangeSet changeSet,
            Dictionary<Element, HashSet<string>> elementsNames,
            Dictionary<Element, ElementVersion> elementsVersions, Dictionary<Element, ElementVersion> oldVersions,
            List<KeyValuePair<Element, List<Tuple<Element, string>>>> removedElements,
            List<KeyValuePair<Element, List<Tuple<Element, string>>>> addedElements)
        {
            var result = new List<KeyValuePair<Element, string>>();
            // we need to keep the name to correctly remove child from elementsNames
            var removedElementsNames = new Dictionary<Element, HashSet<string>>();
            foreach (var pair in removedElements)
            {
                if (!elementsVersions.ContainsKey(pair.Key))
                {
                    Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet,
                                     "Element " + pair.Key + " was removed (or renamed) before any actual version was committed");
                    continue;
                }
                // if this element is also added, we handle it (later) as a rename,
                // but if it was present in several paths that are removed, we only keep the first (visible) one
                bool isRenamed = addedElements.Any(p => p.Key == pair.Key);
                foreach (var namedInElement in pair.Value.ToList())
                {
                    HashSet<string> parentElementNames;
                    if (!elementsNames.TryGetValue(namedInElement.Item1, out parentElementNames) &&
                        !removedElementsNames.TryGetValue(namedInElement.Item1, out parentElementNames))
                        continue;

                    foreach (string parentElementName in parentElementNames)
                    {
                        string elementName = parentElementName + "/" + namedInElement.Item2;

                        // git doesn't handle empty directories...
                        if (!WasEmptyDirectory(pair.Key, elementsVersions, oldVersions))
                        {
                            if (isRenamed)
                            {
                                result.Add(new KeyValuePair<Element, string>(pair.Key, elementName));
                                isRenamed = false;
                            }
                            // maybe one of its parent has already been removed
                            else if (!changeSet.Removed.Any(removed => elementName.StartsWith(removed + "/")))
                                changeSet.Removed.Add(elementName);
                        }
                        // not available anymore
                        RemoveElementName(elementsNames, pair.Key, elementName, removedElementsNames, elementsVersions, oldVersions);
                    }
                }
            }
            return result;
        }

        private static bool WasEmptyDirectory(Element element, Dictionary<Element, ElementVersion> elementsVersions,
            Dictionary<Element, ElementVersion> oldVersions)
        {
            if (!element.IsDirectory)
                return false;
            ElementVersion version;
            // if there has been additions in this changeSet, we look at the version before
            if (!oldVersions.TryGetValue(element, out version))
                if (!elementsVersions.TryGetValue(element, out version))
                    // we never saw a (non-0) version : empty
                    return true;
            
            // we may keep null in oldVersions
            if (version == null)
                return true;

            return ((DirectoryVersion)version).Content.All(v => WasEmptyDirectory(v.Value, elementsVersions, oldVersions));
        }

        private static void ProcessRename(ChangeSet changeSet,
            Dictionary<Element, HashSet<string>> elementsNames,
            Dictionary<Element, ElementVersion> elementsVersions, Dictionary<Element, ElementVersion> oldVersions,
            List<KeyValuePair<Element, string>> renamedElements,
            List<KeyValuePair<Element, List<Tuple<Element, string>>>> addedElements)
        {
            // now elementNames have target names
            // we know that entries in removedElements are ordered from root to leaf
            // we still need to update the old name if a parent directory has already been moved
            // also, we must process all directories before files
            foreach (var pair in renamedElements.Where(p => p.Key.IsDirectory).Union(renamedElements.Where(p => !p.Key.IsDirectory)))
            {
                var oldName = pair.Value;
                // in case of simple rename (without a new version), the old name has not been removed from elementsNames
                elementsNames.RemoveFromCollection(pair.Key, oldName);

                foreach (var rename in changeSet.Renamed)
                {
                    // changeSet.Renamed is in correct order
                    if (oldName.StartsWith(rename.Item1 + "/"))
                        oldName = oldName.Replace(rename.Item1 + "/", rename.Item2 + "/");
                }
                string renamedTo = null;
                int index;
                for (index = 0; index < addedElements.Count; index++)
                    if (addedElements[index].Key == pair.Key)
                        break;

                foreach (var newName in addedElements[index].Value)
                {
                    HashSet<string> elementNames;
                    if (elementsNames.TryGetValue(newName.Item1, out elementNames))
                    {
                        if (!WasEmptyDirectory(pair.Key, elementsVersions, oldVersions))
                        {
                            foreach (string name in elementNames)
                            {
                                if (renamedTo == null)
                                {
                                    renamedTo = name + "/" + newName.Item2;
                                    changeSet.Renamed.Add(new Tuple<string, string>(oldName, renamedTo));
                                    // special case : it may happen that there was another element with the
                                    // destination name, that was simply removed
                                    // in this case the Remove would instead wrongly apply to the renamed
                                    // element, but since the Rename effectively removed the old version,
                                    // we can simply skip it :
                                    changeSet.Removed.Remove(renamedTo);
                                }
                                else
                                    changeSet.Copied.Add(new Tuple<string, string>(renamedTo, name + "/" + newName.Item2));
                            }
                        }
                    }
                    // else destination not visible yet : another (hopefully temporary) orphan
                }
                addedElements.RemoveAt(index);
            }
        }

        private static void AddElement(ChangeSet changeSet, Element element, string baseName, string name,
            Dictionary<Element, ElementVersion> elementsVersions,
            Dictionary<Element, List<Tuple<string, ChangeSet.NamedVersion>>> orphanedVersionsByElement)
        {
            ElementVersion currentVersion;
            if (!elementsVersions.TryGetValue(element, out currentVersion))
                // assumed to be (empty) version 0
                return;

            if (element.IsDirectory)
            {
                foreach (var subElement in ((DirectoryVersion)currentVersion).Content)
                    AddElement(changeSet, subElement.Value, baseName == null ? null : baseName + name + "/", subElement.Key, elementsVersions, orphanedVersionsByElement);
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
                if (fullName != null && !existing[0].Names.Contains(fullName))
                {
                    existing[0].Names.Add(fullName);
                    if (existing[0].Names.Count > 1)
                        Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet,
                            "Version " + existing[0].Version + " has several names : " + string.Join(", ", existing[0].Names));
                }
            }
            else
                addedNamedVersion = changeSet.Add(currentVersion, fullName, false);

            if (addedNamedVersion == null && fullName == null)
                // nothing that interesting happened...
                return;

            if (fullName == null)
            {
                // sadly another orphan
                orphanedVersionsByElement.AddToCollection(element, new Tuple<string, ChangeSet.NamedVersion>(changeSet.Branch, addedNamedVersion));
                return;
            }

            // we've got a name here, maybe we can patch some orphans ?
            List<Tuple<string, ChangeSet.NamedVersion>> orphanedVersions;
            if (!orphanedVersionsByElement.TryGetValue(element, out orphanedVersions))
                // no, no orphan to patch
                return;

            var completed = new List<Tuple<string, ChangeSet.NamedVersion>>();
            foreach (var namedVersion in orphanedVersions)
                if (namedVersion.Item1 == changeSet.Branch && namedVersion.Item2.Version == currentVersion)
                {
                    namedVersion.Item2.Names.Add(fullName);
                    completed.Add(namedVersion);
                }
            foreach (var toRemove in completed)
                orphanedVersions.Remove(toRemove);
            if (orphanedVersions.Count == 0)
                orphanedVersionsByElement.Remove(element);
        }

        private static void RemoveElementName(Dictionary<Element, HashSet<string>> elementsNames, Element element, string elementName,
            Dictionary<Element, HashSet<string>> removedElementsNames, Dictionary<Element, ElementVersion> elementsVersions,
            Dictionary<Element, ElementVersion> oldVersions)
        {
            elementsNames.RemoveFromCollection(element, elementName);
            if (!element.IsDirectory)
                return;
            // so that we may successfully RemoveElementName() of children later
            removedElementsNames.AddToCollection(element, elementName);
            ElementVersion version;
            if (!oldVersions.TryGetValue(element, out version))
                if (!elementsVersions.TryGetValue(element, out version))
                    return;
            if (version == null)
                // found as null in oldVersions
                return;
            var directory = (DirectoryVersion)version;
            foreach (var child in directory.Content)
                RemoveElementName(elementsNames, child.Value, elementName + "/" + child.Key, removedElementsNames, elementsVersions, oldVersions);
        }

        private IEnumerable<string> ProcessLabels(ElementVersion version, Dictionary<Element, ElementVersion> elementsVersions)
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
                    if ((toCheck.VersionNumber == 0 && elementsVersions.ContainsKey(toCheck.Element)) ||
                        (toCheck.VersionNumber != 0 && elementsVersions[toCheck.Element] != toCheck))
                    {
                        Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet,
                            "Label " + label + " is inconsistent : should be on " + toCheck + ", not on " + elementsVersions[toCheck.Element]);
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
