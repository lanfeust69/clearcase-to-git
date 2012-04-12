using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GitImporter
{
    public class CleartoolReader : IDisposable
    {
        public static TraceSource Logger = Program.Logger;

        private static readonly Regex _isFullVersionRegex = new Regex(@"\\\d+$");
        private static readonly Regex _versionRegex = new Regex(@"(.*)\@\@(\\main(\\[\w\.]+)*\\\d+)$");

        private readonly Cleartool _cleartool = new Cleartool();

        public Dictionary<string, Element> ElementsByOid { get; private set; }

        private List<Tuple<DirectoryVersion, string, string>> _fixups = new List<Tuple<DirectoryVersion, string, string>>();

        public CleartoolReader(string clearcaseRoot)
        {
            _cleartool.Cd(clearcaseRoot);
        }

        public VobDB VobDB { get { return new VobDB(ElementsByOid); } }

        internal void Init(VobDB vobDB, IEnumerable<Element> elements)
        {
            ElementsByOid = vobDB != null ? vobDB.ElementsByOid : new Dictionary<string, Element>();

            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadCleartool, "Start fetching oids of exported elements");
            int i = 0;
            foreach (var element in elements)
            {
                if (++i % 500 == 0)
                    Logger.TraceData(TraceEventType.Information, (int)TraceId.ReadCleartool, "Fetching oid for element " + i);
                string oid = _cleartool.GetOid(element.Name);
                if (string.IsNullOrEmpty(oid))
                {
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.ReadCleartool, "Could not find oid for element " + element.Name);
                    continue;
                }
                element.Oid = oid;
                ElementsByOid[oid] = element;
            }
            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ReadCleartool, "Stop fetching oids of exported elements");
        }

        public void Read(string directoriesFile, string elementsFile, string versionsFile)
        {
            if (!string.IsNullOrWhiteSpace(elementsFile))
            {
                Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadCleartool, "Start reading file elements", elementsFile);
                using (var files = new StreamReader(elementsFile))
                {
                    string line;
                    int i = 0;
                    while ((line = files.ReadLine()) != null)
                    {
                        if (++i % 100 == 0)
                            Logger.TraceData(TraceEventType.Information, (int)TraceId.ReadCleartool, "Reading file element " + i);
                        ReadElement(line, false);
                    }
                }
                Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ReadCleartool, "Stop reading file elements", elementsFile);
            }

            if (!string.IsNullOrWhiteSpace(directoriesFile))
            {
                Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadCleartool, "Start reading directory elements", directoriesFile);
                using (var directories = new StreamReader(directoriesFile))
                {
                    string line;
                    int i = 0;
                    while ((line = directories.ReadLine()) != null)
                    {
                        if (++i % 20 == 0)
                            Logger.TraceData(TraceEventType.Information, (int)TraceId.ReadCleartool, "Reading directory element " + i);
                        ReadElement(line, true);
                    }
                }
                Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ReadCleartool, "Stop reading directory elements", directoriesFile);
            }

            if (!string.IsNullOrWhiteSpace(versionsFile))
            {
                Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadCleartool, "Start reading individual versions", versionsFile);
                using (var versions = new StreamReader(versionsFile))
                {
                    string line;
                    int i = 0;
                    while ((line = versions.ReadLine()) != null)
                    {
                        if (++i % 100 == 0)
                            Logger.TraceData(TraceEventType.Information, (int)TraceId.ReadCleartool, "Reading version " + i);
                        ReadVersion(line);
                    }
                }
                Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ReadCleartool, "Stop reading individual versions", versionsFile);
            }

            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadCleartool, "Start fixups");
            foreach (var fixup in _fixups)
            {
                Element childElement;
                if (ElementsByOid.TryGetValue(fixup.Item3, out childElement))
                    fixup.Item1.Content.Add(new KeyValuePair<string, Element>(fixup.Item2, childElement));
                else
                    Logger.TraceData(TraceEventType.Verbose, (int)TraceId.ReadCleartool,
                                     "Element " + fixup.Item2 + " (oid:" + fixup.Item3 + ") referenced as " + fixup.Item2 + " in " + fixup.Item1 + " was not imported");
            }
            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ReadCleartool, "Stop fixups");
        }

        private void ReadElement(string elementName, bool isDir)
        {
            // canonical name of elements is without the trailing '@@'
            if (elementName.EndsWith("@@"))
                elementName = elementName.Substring(0, elementName.Length - 2);
            string oid = _cleartool.GetOid(elementName);
            if (string.IsNullOrEmpty(oid))
            {
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.ReadCleartool, "Could not find oid for element " + elementName);
                return;
            }
            if (ElementsByOid.ContainsKey(oid))
                return;

            Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.ReadCleartool,
                "Start reading " + (isDir ? "directory" : "file") + " element", elementName);
            var element = new Element(elementName, isDir) { Oid = oid };
            ElementsByOid[oid] = element;
            foreach (string versionString in _cleartool.Lsvtree(elementName))
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
                AddVersionToBranch(branch, versionNumber, isDir);
            }
            Logger.TraceData(TraceEventType.Stop | TraceEventType.Verbose, (int)TraceId.ReadCleartool, "Stop reading element", elementName);
        }

        private void AddVersionToBranch(ElementBranch branch, int versionNumber, bool isDir)
        {
            ElementVersion version;
            if (isDir)
            {
                var dirVersion = new DirectoryVersion(branch, versionNumber);
                var res = _cleartool.Ls(dirVersion.ToString());
                foreach (var child in res)
                {
                    Element childElement;
                    if (ElementsByOid.TryGetValue(child.Value, out childElement))
                        dirVersion.Content.Add(new KeyValuePair<string, Element>(child.Key, childElement));
                    else if (child.Value.StartsWith(SymLinkElement.SYMLINK))
                    {
                        var symLink = new SymLinkElement(dirVersion, child.Value);
                        ElementsByOid.Add(symLink.Oid, symLink);
                        dirVersion.Content.Add(new KeyValuePair<string, Element>(child.Key, symLink));
                    }
                    else
                        _fixups.Add(new Tuple<DirectoryVersion, string, string>(dirVersion, child.Key, child.Value));
                }
                version = dirVersion;
            }
            else
                version = new ElementVersion(branch, versionNumber);
            _cleartool.GetVersionDetails(version);
            branch.Versions.Add(version);
        }

        private void ReadVersion(string version)
        {
            Match match = _versionRegex.Match(version);
            if (!match.Success)
            {
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.ReadCleartool, "Could not parse '" + version + "' as a clearcase version");
                return;
            }

            string elementName = match.Groups[1].Value;
            bool isDir;
            string oid = _cleartool.GetOid(elementName, out isDir);
            if (string.IsNullOrEmpty(oid))
            {
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.ReadCleartool, "Could not find oid for element " + elementName);
                return;
            }
            Element element;
            if (!ElementsByOid.TryGetValue(oid, out element))
            {
                element = new Element(elementName, isDir) { Oid = oid };
                ElementsByOid.Add(oid, element);
            }
            else if (element.Name != elementName)
            {
                // the element is now seen with a different name in the currently used view
                Logger.TraceData(TraceEventType.Information, (int)TraceId.ReadCleartool,
                    string.Format("element with oid {0} has a different name : now using {1}instead of {2}", oid, elementName, element.Name));
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
            string previousVersion = _cleartool.GetPredecessor(version);
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
                ReadVersion(elementName + "@@" + previousVersion);
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
            
            AddVersionToBranch(branch, versionNumber, isDir);

            Logger.TraceData(TraceEventType.Stop | TraceEventType.Verbose, (int)TraceId.ReadCleartool, "Stop reading version", version);            
        }

        public void Dispose()
        {
            _cleartool.Dispose();
        }
    }
}
