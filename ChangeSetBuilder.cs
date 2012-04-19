using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace GitImporter
{
    public class ChangeSetBuilder
    {
        public static TraceSource Logger = Program.Logger;

        private readonly ChangeSet _changeSet;
        private readonly Dictionary<Element, HashSet<string>> _elementsNames;
        private readonly Dictionary<Element, ElementVersion> _elementsVersions;
        private Dictionary<Element, ElementVersion> _oldVersions;
        private readonly Dictionary<Element, List<Tuple<string, ChangeSet.NamedVersion>>> _orphanedVersionsByElement;
        private readonly HashSet<string> _roots;

        public ChangeSetBuilder(ChangeSet changeSet, Dictionary<Element, HashSet<string>> elementsNames, Dictionary<Element, ElementVersion> elementsVersions, Dictionary<Element, List<Tuple<string, ChangeSet.NamedVersion>>> orphanedVersionsByElement, HashSet<string> roots)
        {
            _changeSet = changeSet;
            _elementsVersions = elementsVersions;
            _elementsNames = elementsNames;
            _orphanedVersionsByElement = orphanedVersionsByElement;
            _roots = roots;
        }

        public void Build()
        {
            // first update current version of changed elements, but keep old versions to handle remove/rename
            _oldVersions = new Dictionary<Element, ElementVersion>();
            foreach (var namedVersion in _changeSet.Versions)
            {
                ElementVersion oldVersion;
                _elementsVersions.TryGetValue(namedVersion.Version.Element, out oldVersion);
                // we keep track that there was no previous version : null
                if (!_oldVersions.ContainsKey(namedVersion.Version.Element))
                    _oldVersions.Add(namedVersion.Version.Element, oldVersion);
                _elementsVersions[namedVersion.Version.Element] = namedVersion.Version;
            }

            ProcessDirectoryChanges();

            foreach (var namedVersion in _changeSet.Versions)
            {
                if (namedVersion.Names.Count > 0)
                    continue;

                HashSet<string> elementNames;
                if (!_elementsNames.TryGetValue(namedVersion.Version.Element, out elementNames))
                {
                    if (namedVersion.Names.Count > 0)
                        throw new Exception("Version " + namedVersion.Version + " was named " + namedVersion.Names[0] + ", but had no entry in elementNames");

                    Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet,
                        "Version " + namedVersion.Version + " was not yet visible in an existing directory version");
                    _orphanedVersionsByElement.AddToCollection(namedVersion.Version.Element,
                        new Tuple<string, ChangeSet.NamedVersion>(_changeSet.Branch, namedVersion));
                    continue;
                }
                namedVersion.Names.AddRange(elementNames);
            }
        }

        private void ProcessDirectoryChanges()
        {
            // first order from roots to leaves (because changes to roots also impact leaves)
            var unorderedVersions = new List<DirectoryVersion>(_changeSet.Versions.Select(v => v.Version).OfType<DirectoryVersion>());
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

            var renamedElements = ProcessRemove(removedElements, addedElements);

            // then update elementNames so that later changes of the elements will be at correct location
            foreach (var version in orderedVersions)
            {
                // here, we want to process only the most recent version (if there was several)
                if (orderedVersions.Any(v => v.Element == version.Element && v.VersionNumber > version.VersionNumber))
                    continue;
                HashSet<string> elementNames;
                if (!_elementsNames.TryGetValue(version.Element, out elementNames))
                {
                    if (_roots.Contains(version.Element.Name))
                    {
                        elementNames = new HashSet<string> { version.Element.Name.Replace('\\', '/') };
                        _elementsNames.Add(version.Element, elementNames);
                    }
                    else
                        // removed by one of the changes
                        continue;
                }
                foreach (string baseName in elementNames)
                    UpdateChildNames(version, baseName + "/");
            }

            ProcessRename(renamedElements, addedElements);

            // now remaining added elements
            foreach (var pair in addedElements)
                foreach (var namedInElement in pair.Value)
                {
                    HashSet<string> baseNames;
                    if (_elementsNames.TryGetValue(namedInElement.Item1, out baseNames))
                        baseNames = new HashSet<string>(baseNames.Select(s => s + "/"));
                    else
                        baseNames = new HashSet<string> { null };
                    foreach (string baseName in baseNames)
                        AddElement(pair.Key, baseName, namedInElement.Item2);
                }
        }

        private void UpdateChildNames(DirectoryVersion version, string baseName)
        {
            foreach (var child in version.Content)
            {
                _elementsNames.AddToCollection(child.Value, baseName + child.Key);
                ElementVersion childVersion;
                if (child.Value.IsDirectory && _elementsVersions.TryGetValue(child.Value, out childVersion))
                    UpdateChildNames((DirectoryVersion)childVersion, baseName + child.Key + "/");
            }
        }

        private static void ComputeDiffWithPrevious(DirectoryVersion version,
            List<KeyValuePair<Element, List<Tuple<Element, string>>>> removedElements,
            List<KeyValuePair<Element, List<Tuple<Element, string>>>> addedElements)
        {
            // we never put (uninteresting) version 0 of a directory element in a changeSet,
            // but it is still in the ElementBranch.Versions
            var previousVersion = (DirectoryVersion)version.GetPreviousVersion();
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
        private List<KeyValuePair<Element, string>> ProcessRemove(
            List<KeyValuePair<Element, List<Tuple<Element, string>>>> removedElements,
            List<KeyValuePair<Element, List<Tuple<Element, string>>>> addedElements)
        {
            var result = new List<KeyValuePair<Element, string>>();
            // we need to keep the name to correctly remove child from elementsNames
            var removedElementsNames = new Dictionary<Element, HashSet<string>>();
            foreach (var pair in removedElements)
            {
                if (!_elementsVersions.ContainsKey(pair.Key))
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
                    if (!_elementsNames.TryGetValue(namedInElement.Item1, out parentElementNames) &&
                        !removedElementsNames.TryGetValue(namedInElement.Item1, out parentElementNames))
                        continue;

                    foreach (string parentElementName in parentElementNames)
                    {
                        string elementName = parentElementName + "/" + namedInElement.Item2;

                        // git doesn't handle empty directories...
                        if (!WasEmptyDirectory(pair.Key))
                        {
                            if (isRenamed)
                            {
                                result.Add(new KeyValuePair<Element, string>(pair.Key, elementName));
                                isRenamed = false;
                            }
                            // maybe one of its parent has already been removed
                            else if (!_changeSet.Removed.Any(removed => elementName.StartsWith(removed + "/")))
                                _changeSet.Removed.Add(elementName);
                        }
                        // not available anymore
                        RemoveElementName(pair.Key, elementName, removedElementsNames);
                    }
                }
            }
            return result;
        }

        private bool WasEmptyDirectory(Element element)
        {
            if (!element.IsDirectory)
                return false;
            ElementVersion version;
            // if there has been additions in this changeSet, we look at the version before
            if (!_oldVersions.TryGetValue(element, out version))
                if (!_elementsVersions.TryGetValue(element, out version))
                    // we never saw a (non-0) version : empty
                    return true;
            
            // we may keep null in oldVersions
            if (version == null)
                return true;

            return ((DirectoryVersion)version).Content.All(v => WasEmptyDirectory(v.Value));
        }

        private void ProcessRename(
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
                _elementsNames.RemoveFromCollection(pair.Key, oldName);

                foreach (var rename in _changeSet.Renamed)
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
                    if (_elementsNames.TryGetValue(newName.Item1, out elementNames))
                    {
                        if (!WasEmptyDirectory(pair.Key))
                        {
                            foreach (string name in elementNames)
                            {
                                if (renamedTo == null)
                                {
                                    renamedTo = name + "/" + newName.Item2;
                                    _changeSet.Renamed.Add(new Tuple<string, string>(oldName, renamedTo));
                                    // special case : it may happen that there was another element with the
                                    // destination name, that was simply removed
                                    // in this case the Remove would instead wrongly apply to the renamed
                                    // element, but since the Rename effectively removed the old version,
                                    // we can simply skip it :
                                    _changeSet.Removed.Remove(renamedTo);
                                }
                                else
                                    _changeSet.Copied.Add(new Tuple<string, string>(renamedTo, name + "/" + newName.Item2));
                            }
                        }
                    }
                    // else destination not visible yet : another (hopefully temporary) orphan
                }
                addedElements.RemoveAt(index);
            }
        }

        private void AddElement(Element element, string baseName, string name)
        {
            ElementVersion currentVersion;
            if (!_elementsVersions.TryGetValue(element, out currentVersion))
                // assumed to be (empty) version 0
                return;

            if (element.IsDirectory)
            {
                foreach (var subElement in ((DirectoryVersion)currentVersion).Content)
                    AddElement(subElement.Value, baseName == null ? null : baseName + name + "/", subElement.Key);
                return;
            }
            List<ChangeSet.NamedVersion> existing = _changeSet.Versions.Where(v => v.Version.Element == element).ToList();
            ChangeSet.NamedVersion addedNamedVersion = null;
            if (existing.Count > 1)
                throw new Exception("Unexpected number of versions (" + existing.Count + ") of file element " + element + " in change set " + _changeSet);

            string fullName = baseName == null ? null : baseName + name;
            if (existing.Count == 1)
            {
                if (existing[0].Version != currentVersion)
                    throw new Exception("Unexpected mismatch of versions of file element " + element + " in change set " + _changeSet + " : " + existing[0].Version + " != " + currentVersion);
                if (fullName != null && !existing[0].Names.Contains(fullName))
                {
                    existing[0].Names.Add(fullName);
                    if (existing[0].Names.Count > 1)
                        Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet,
                            "Version " + existing[0].Version + " has several names : " + string.Join(", ", existing[0].Names));
                }
            }
            else
                addedNamedVersion = _changeSet.Add(currentVersion, fullName, false);

            if (addedNamedVersion == null && fullName == null)
                // nothing that interesting happened...
                return;

            if (fullName == null)
            {
                // sadly another orphan
                _orphanedVersionsByElement.AddToCollection(element, new Tuple<string, ChangeSet.NamedVersion>(_changeSet.Branch, addedNamedVersion));
                return;
            }

            // we've got a name here, maybe we can patch some orphans ?
            List<Tuple<string, ChangeSet.NamedVersion>> orphanedVersions;
            if (!_orphanedVersionsByElement.TryGetValue(element, out orphanedVersions))
                // no, no orphan to patch
                return;

            var completed = new List<Tuple<string, ChangeSet.NamedVersion>>();
            foreach (var namedVersion in orphanedVersions)
                if (namedVersion.Item1 == _changeSet.Branch && namedVersion.Item2.Version == currentVersion)
                {
                    namedVersion.Item2.Names.Add(fullName);
                    completed.Add(namedVersion);
                }
            foreach (var toRemove in completed)
                orphanedVersions.Remove(toRemove);
            if (orphanedVersions.Count == 0)
                _orphanedVersionsByElement.Remove(element);
        }

        private void RemoveElementName(Element element, string elementName,
            Dictionary<Element, HashSet<string>> removedElementsNames)
        {
            _elementsNames.RemoveFromCollection(element, elementName);
            if (!element.IsDirectory)
                return;
            // so that we may successfully RemoveElementName() of children later
            removedElementsNames.AddToCollection(element, elementName);
            ElementVersion version;
            if (!_oldVersions.TryGetValue(element, out version))
                if (!_elementsVersions.TryGetValue(element, out version))
                    return;
            if (version == null)
                // found as null in oldVersions
                return;
            var directory = (DirectoryVersion)version;
            foreach (var child in directory.Content)
                RemoveElementName(child.Value, elementName + "/" + child.Key, removedElementsNames);
        }
    }
}
