using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GitImporter
{
    public class CleartoolReader : IDisposable
    {
        public static TraceSource Logger = Program.Logger;

        private static readonly Regex _isFullVersionRegex = new Regex(@"\\\d+$");
        private static readonly Regex _versionRegex = new Regex(@"(.*)\@\@(\\main(\\[\w\.]+)*\\\d+)$");

        private const int _nbCleartool = 10;
        private readonly Cleartool[] _cleartools;
        private readonly DateTime _originDate;

        public Dictionary<string, Element> ElementsByOid { get; private set; }
        private readonly HashSet<string> _oidsToCheck = new HashSet<string>();

        private readonly List<Tuple<DirectoryVersion, string, string>> _contentFixups = new List<Tuple<DirectoryVersion, string, string>>();
        private readonly List<Tuple<ElementVersion, string, int, bool>> _mergeFixups = new List<Tuple<ElementVersion, string, int, bool>>();

        public CleartoolReader(string clearcaseRoot, string originDate)
        {
            _cleartools = new Cleartool[_nbCleartool];
            for (int i = 0; i < _nbCleartool; i++)
                _cleartools[i] = new Cleartool(clearcaseRoot);

            _originDate = string.IsNullOrEmpty(originDate) ? DateTime.UtcNow : DateTime.Parse(originDate).ToUniversalTime();
        }

        public VobDB VobDB { get { return new VobDB(ElementsByOid); } }

        internal void Init(VobDB vobDB, IEnumerable<Element> elements)
        {
            ElementsByOid = vobDB != null ? vobDB.ElementsByOid : new Dictionary<string, Element>();

            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadCleartool, "Start fetching oids of exported elements");
            int i = 0;
            var allActions = new List<Action>();
            foreach (var element in elements)
            {
                int iTask = ++i;
                Element currentElement = element;
                allActions.Add(() =>
                    {
                        if (iTask % 500 == 0)
                            Logger.TraceData(TraceEventType.Information, (int)TraceId.ReadCleartool, "Fetching oid for element " + iTask);
                        string oid;
                        lock (_cleartools[iTask % _nbCleartool])
                            oid = _cleartools[iTask % _nbCleartool].GetOid(currentElement.Name);
                        if (string.IsNullOrEmpty(oid))
                        {
                            Logger.TraceData(TraceEventType.Warning, (int)TraceId.ReadCleartool, "Could not find oid for element " + currentElement.Name);
                            return;
                        }
                        currentElement.Oid = oid;
                        lock (ElementsByOid)
                            ElementsByOid[oid] = currentElement;
                        // these elements come from a non-filtered clearcase export : there may be unwanted elements
                        lock (_oidsToCheck)
                            _oidsToCheck.Add(oid);
                    });
            }
            Parallel.Invoke(new ParallelOptions { MaxDegreeOfParallelism = _nbCleartool * 2 }, allActions.ToArray());
            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ReadCleartool, "Stop fetching oids of exported elements");
        }

        public List<ElementVersion> Read(string directoriesFile, string elementsFile, string versionsFile)
        {
            List<ElementVersion> result = null;
            if (!string.IsNullOrWhiteSpace(elementsFile))
            {
                Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadCleartool, "Start reading file elements", elementsFile);
                var allActions = new List<Action>();
                using (var files = new StreamReader(elementsFile))
                {
                    string line;
                    int i = 0;
                    while ((line = files.ReadLine()) != null)
                    {
                        int iTask = ++i;
                        string currentLine = line;
                        allActions.Add(() =>
                            {
                                if (iTask % 100 == 0)
                                    Logger.TraceData(TraceEventType.Information, (int)TraceId.ReadCleartool, "Reading file element " + iTask);
                                ReadElement(currentLine, false, _cleartools[iTask % _nbCleartool]);
                            });
                    }
                }
                Parallel.Invoke(new ParallelOptions { MaxDegreeOfParallelism = _nbCleartool * 2 }, allActions.ToArray());
                Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ReadCleartool, "Stop reading file elements", elementsFile);
            }

            if (!string.IsNullOrWhiteSpace(directoriesFile))
            {
                Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadCleartool, "Start reading directory elements", directoriesFile);
                var allActions = new List<Action>();
                using (var directories = new StreamReader(directoriesFile))
                {
                    string line;
                    int i = 0;
                    while ((line = directories.ReadLine()) != null)
                    {
                        int iTask = ++i;
                        string currentLine = line;
                        allActions.Add(() =>
                            {
                                if (iTask % 20 == 0)
                                    Logger.TraceData(TraceEventType.Information, (int)TraceId.ReadCleartool, "Reading directory element " + iTask);
                                ReadElement(currentLine, true, _cleartools[iTask % _nbCleartool]);
                            });
                    }
                }
                Parallel.Invoke(new ParallelOptions { MaxDegreeOfParallelism = _nbCleartool * 2 }, allActions.ToArray());
                Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ReadCleartool, "Stop reading directory elements", directoriesFile);
            }

            if (!string.IsNullOrWhiteSpace(versionsFile))
            {
                Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadCleartool, "Start reading individual versions", versionsFile);
                result = new List<ElementVersion>();
                using (var versions = new StreamReader(versionsFile))
                {
                    string line;
                    int i = 0;
                    // not parallel because not as useful, and trickier to handle versions in "random" order
                    while ((line = versions.ReadLine()) != null)
                    {
                        if (++i % 100 == 0)
                            Logger.TraceData(TraceEventType.Information, (int)TraceId.ReadCleartool, "Reading version " + i);
                        ReadVersion(line, result, _cleartools[i % _nbCleartool]);
                    }
                }
                Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ReadCleartool, "Stop reading individual versions", versionsFile);
            }

            // oids still in _oidsToCheck are not to be really imported
            if (_oidsToCheck.Count > 0)
            {
                foreach (var oid in _oidsToCheck)
                    ElementsByOid.Remove(oid);
                foreach (var directory in ElementsByOid.Values.Where(e => e.IsDirectory))
                    foreach (var branch in directory.Branches.Values)
                        foreach (DirectoryVersion directoryVersion in branch.Versions)
                        {
                            // use a copy to be able to remove
                            var original = directoryVersion.Content.ToList();
                            directoryVersion.Content.Clear();
                            directoryVersion.Content.AddRange(original.Where(p => !_oidsToCheck.Contains(p.Value.Oid)));
                        }
            }

            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadCleartool, "Start fixups");
            foreach (var fixup in _contentFixups)
            {
                Element childElement;
                if (ElementsByOid.TryGetValue(fixup.Item3, out childElement))
                    fixup.Item1.Content.Add(new KeyValuePair<string, Element>(fixup.Item2, childElement));
                else
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.ReadCleartool,
                                     "Element " + fixup.Item2 + " (oid:" + fixup.Item3 + ") referenced as " + fixup.Item2 + " in " + fixup.Item1 + " was not imported");
            }
            foreach (var fixup in _mergeFixups)
            {
                ElementVersion toFix = fixup.Item1;
                ElementVersion linkTo = toFix.Element.GetVersion(fixup.Item2, fixup.Item3);
                if (linkTo == null)
                {
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.ReadCleartool,
                                     "Version " + fixup.Item2 + "/" + fixup.Item3 + " of " + toFix.Element +
                                     ", linked to " + toFix.Branch.BranchName + "/" + toFix.VersionNumber + ", was not imported");
                    continue;
                }
                (fixup.Item4 ? toFix.MergesTo : toFix.MergesFrom).Add(linkTo);
            }
            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ReadCleartool, "Stop fixups");
            return result;
        }

        private void ReadElement(string elementName, bool isDir, Cleartool cleartool)
        {
            // canonical name of elements is without the trailing '@@'
            if (elementName.EndsWith("@@"))
                elementName = elementName.Substring(0, elementName.Length - 2);
            string oid;
            lock (cleartool)
                oid = cleartool.GetOid(elementName);
            if (string.IsNullOrEmpty(oid))
            {
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.ReadCleartool, "Could not find oid for element " + elementName);
                return;
            }
            lock (_oidsToCheck)
                _oidsToCheck.Remove(oid);
            lock (ElementsByOid)
                if (ElementsByOid.ContainsKey(oid))
                    return;

            Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.ReadCleartool,
                "Start reading " + (isDir ? "directory" : "file") + " element", elementName);
            var element = new Element(elementName, isDir) { Oid = oid };
            lock (ElementsByOid)
                ElementsByOid[oid] = element;
            List<string> versionStrings;
            lock (cleartool)
                versionStrings = cleartool.Lsvtree(elementName);
            foreach (string versionString in versionStrings)
            {
                // there is a first "version" for each branch, without a version number
                if (!_isFullVersionRegex.IsMatch(versionString))
                    continue;
                Logger.TraceData(TraceEventType.Verbose, (int)TraceId.ReadCleartool, "Creating version", versionString);
                string[] versionPath = versionString.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                string branchName = versionPath[versionPath.Length - 2];
                int versionNumber = int.Parse(versionPath[versionPath.Length - 1]);
                ElementBranch branch;
                if (!element.Branches.TryGetValue(branchName, out branch))
                {
                    // a new branch always start from the last seen version of the parent branch
                    ElementVersion branchingPoint = null;
                    if (versionPath.Length > 2)
                        branchingPoint = element.Branches[versionPath[versionPath.Length - 3]].Versions.Last();
                    branch = new ElementBranch(element, branchName, branchingPoint);
                    element.Branches[branchName] = branch;
                }
                bool added = AddVersionToBranch(branch, versionNumber, isDir, null, cleartool);
                if (!added)
                {
                    // versions was too recent
                    if (branch.Versions.Count == 0)
                        // do not leave an empty branch
                        element.Branches.Remove(branchName);
                    // versions are retrieved in order of creation only within a branch :
                    // we still may have eligible versions on a parent branch, so we must continue
                }
            }
            Logger.TraceData(TraceEventType.Stop | TraceEventType.Verbose, (int)TraceId.ReadCleartool, "Stop reading element", elementName);
        }

        private bool AddVersionToBranch(ElementBranch branch, int versionNumber, bool isDir, List<ElementVersion> newVersions, Cleartool cleartool)
        {
            ElementVersion version;
            if (isDir)
            {
                var dirVersion = new DirectoryVersion(branch, versionNumber);
                Dictionary<string, string> res;
                lock (cleartool)
                    res = cleartool.Ls(dirVersion.ToString());
                foreach (var child in res)
                    lock (ElementsByOid)
                    {
                        Element childElement;
                        if (ElementsByOid.TryGetValue(child.Value, out childElement))
                            dirVersion.Content.Add(new KeyValuePair<string, Element>(child.Key, childElement));
                        else if (child.Value.StartsWith(SymLinkElement.SYMLINK))
                        {
                            Element symLink = new SymLinkElement(branch.Element, child.Value);
                            Element existing;
                            if (ElementsByOid.TryGetValue(symLink.Oid, out existing))
                                symLink = existing;
                            else
                                ElementsByOid.Add(symLink.Oid, symLink);
                            dirVersion.Content.Add(new KeyValuePair<string, Element>(child.Key, symLink));
                        }
                        else
                            _contentFixups.Add(new Tuple<DirectoryVersion, string, string>(dirVersion, child.Key, child.Value));
                    }

                version = dirVersion;
            }
            else
                version = new ElementVersion(branch, versionNumber);
            List<Tuple<string, int>> mergesTo, mergesFrom;
            lock (cleartool)
                cleartool.GetVersionDetails(version, out mergesTo, out mergesFrom);
            if (mergesTo != null)
                foreach (var merge in mergesTo)
                    // only merges between branches are interesting
                    if (merge.Item1 != branch.BranchName)
                        lock (_mergeFixups)
                            _mergeFixups.Add(new Tuple<ElementVersion, string, int, bool>(version, merge.Item1, merge.Item2, true));
            if (mergesFrom != null)
                foreach (var merge in mergesFrom)
                    // only merges between branches are interesting
                    if (merge.Item1 != branch.BranchName)
                        lock (_mergeFixups)
                            _mergeFixups.Add(new Tuple<ElementVersion, string, int, bool>(version, merge.Item1, merge.Item2, false));

            if (version.Date > _originDate)
            {
                Logger.TraceData(TraceEventType.Information, (int)TraceId.ReadCleartool,
                    string.Format("Skipping version {0} : {1} > {2}", version, version.Date, _originDate));
                return false;
            }

            branch.Versions.Add(version);
            if (newVersions != null)
                newVersions.Add(version);
            return true;
        }

        private void ReadVersion(string version, List<ElementVersion> newVersions, Cleartool cleartool)
        {
            Match match = _versionRegex.Match(version);
            if (!match.Success)
            {
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.ReadCleartool, "Could not parse '" + version + "' as a clearcase version");
                return;
            }

            string elementName = match.Groups[1].Value;
            bool isDir;
            string oid;
            lock (cleartool)
                oid = cleartool.GetOid(elementName, out isDir);
            if (string.IsNullOrEmpty(oid))
            {
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.ReadCleartool, "Could not find oid for element " + elementName);
                return;
            }
            lock (_oidsToCheck)
                _oidsToCheck.Remove(oid);
            Element element;
            lock (ElementsByOid)
                if (!ElementsByOid.TryGetValue(oid, out element))
                {
                    element = new Element(elementName, isDir) { Oid = oid };
                    ElementsByOid.Add(oid, element);
                }
                else if (element.Name != elementName)
                {
                    // the element is now seen with a different name in the currently used view
                    Logger.TraceData(TraceEventType.Information, (int)TraceId.ReadCleartool,
                        string.Format("element with oid {0} has a different name : now using {1} instead of {2}", oid, elementName, element.Name));
                    element.Name = elementName;
                }
            string[] versionPath = match.Groups[2].Value.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            string branchName = versionPath[versionPath.Length - 2];
            int versionNumber = int.Parse(versionPath[versionPath.Length - 1]);
            // since we call ourself recursively to check the previous version, we first check the recursion end condition
            ElementBranch branch;
            if (element.Branches.TryGetValue(branchName, out branch) && branch.Versions.Count > 0 &&
                branch.Versions.Last().VersionNumber >= versionNumber)
                // already read
                return;

            Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.ReadCleartool, "Start reading version", version);
            string previousVersion;
            lock (cleartool)
                previousVersion = cleartool.GetPredecessor(version);
            int previousVersionNumber = -1;
            if (previousVersion == null)
            {
                if (branchName != "main" || versionNumber != 0)
                    throw new Exception("Failed to retrieve predecessor of " + version);
                branch = new ElementBranch(element, branchName, null);
                element.Branches[branchName] = branch;
            }
            else
            {
                ReadVersion(elementName + "@@" + previousVersion, newVersions, cleartool);
                string[] parts = previousVersion.Split('\\');
                previousVersionNumber = int.Parse(parts[parts.Length - 1]);
            }

            if (!element.Branches.TryGetValue(branchName, out branch))
            {
                if (versionNumber != 0)
                    // we should have completed in ReadVersion(elementName + "@@" + previousVersion)
                    throw new Exception("Could not complete branch " + branchName);

                ElementVersion branchingPoint = null;
                if (versionPath.Length > 2)
                {
                    string parentBranchName = versionPath[versionPath.Length - 3];
                    ElementBranch parentBranch;
                    if (!element.Branches.TryGetValue(parentBranchName, out parentBranch) ||
                        (branchingPoint = parentBranch.Versions.FirstOrDefault(v => v.VersionNumber == previousVersionNumber)) == null)
                        throw new Exception("Could not complete branch " + parentBranchName);
                }
                branch = new ElementBranch(element, branchName, branchingPoint);
                element.Branches[branchName] = branch;
            }
            
            bool added = AddVersionToBranch(branch, versionNumber, isDir, newVersions, cleartool);
            if (!added && branch.Versions.Count == 0)
                // do not leave an empty branch
                element.Branches.Remove(branchName);

            Logger.TraceData(TraceEventType.Stop | TraceEventType.Verbose, (int)TraceId.ReadCleartool, "Stop reading version", version);            
        }

        public void Dispose()
        {
            foreach (var cleartool in _cleartools)
                cleartool.Dispose();
        }
    }
}
