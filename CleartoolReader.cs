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

        private static readonly Regex _fullVersionRegex = new Regex(@"\\\d+$");

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

            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadCleartool, "start fetching oids of exported elements");
            foreach (var element in elements)
            {
                string oid = _cleartool.GetOid(element.Name);
                if (string.IsNullOrEmpty(oid))
                {
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.ReadCleartool, "could not find oid for element " + element.Name);
                    continue;
                }
                element.Oid = oid;
                ElementsByOid[oid] = element;
            }
            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ReadCleartool, "stop fetching oids of exported elements");
        }

        public void Read(string directoriesFile, string elementsFile)
        {
            if (!string.IsNullOrWhiteSpace(elementsFile))
            {
                Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadCleartool, "start reading file elements", elementsFile);
                using (var files = new StreamReader(elementsFile))
                {
                    string line;
                    while ((line = files.ReadLine()) != null)
                        ReadElement(line, false);
                }
                Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ReadCleartool, "stop reading file elements", elementsFile);
            }
            if (!string.IsNullOrWhiteSpace(directoriesFile))
            {
                Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadCleartool, "start reading directory elements", directoriesFile);
                using (var directories = new StreamReader(directoriesFile))
                {
                    string line;
                    while ((line = directories.ReadLine()) != null)
                        ReadElement(line, true);
                }
                Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ReadCleartool, "stop reading directory elements", directoriesFile);
            }

            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadCleartool, "start fixups");
            foreach (var fixup in _fixups)
            {
                Element childElement;
                if (ElementsByOid.TryGetValue(fixup.Item3, out childElement))
                    fixup.Item1.Content.Add(new KeyValuePair<string, Element>(fixup.Item2, childElement));
                else
                    Logger.TraceData(TraceEventType.Verbose, (int)TraceId.ReadCleartool,
                                     "element " + fixup.Item2 + " (oid:" + fixup.Item3 + ") referenced as " + fixup.Item2 + " in " + fixup.Item1 + " was not imported");
            }
            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ReadCleartool, "stop fixups");
        }

        private void ReadElement(string elementName, bool isDir)
        {
            // canonical name of elements is without the trailing '@@'
            if (elementName.EndsWith("@@"))
                elementName = elementName.Substring(0, elementName.Length - 2);
            string oid = _cleartool.GetOid(elementName);
            if (string.IsNullOrEmpty(oid))
            {
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.ReadCleartool, "could not find oid for element " + elementName);
                return;
            }
            if (string.IsNullOrEmpty(oid) || ElementsByOid.ContainsKey(oid))
                return;

            Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.ReadCleartool,
                "start reading " + (isDir ? "directory" : "file") + " element", elementName);
            var element = new Element(elementName, isDir) { Oid = oid };
            ElementsByOid[oid] = element;
            foreach (string versionString in _cleartool.Lsvtree(elementName))
            {
                // there is a first "version" for each branch, without a version number
                if (!_fullVersionRegex.IsMatch(versionString))
                    continue;
                Logger.TraceData(TraceEventType.Verbose, (int)TraceId.ReadCleartool, "creating version", versionString);
                string[] versionPath = versionString.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                string branchName = versionPath[versionPath.Length - 2];
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
                ElementVersion version;
                if (isDir)
                {
                    var dirVersion = new DirectoryVersion(branch, int.Parse(versionPath[versionPath.Length - 1]));
                    var res = _cleartool.Ls(dirVersion.ToString());
                    foreach (var child in res)
                    {
                        Element childElement;
                        if (ElementsByOid.TryGetValue(child.Value, out childElement))
                            dirVersion.Content.Add(new KeyValuePair<string, Element>(child.Key, childElement));
                        else
                            _fixups.Add(new Tuple<DirectoryVersion, string, string>(dirVersion, child.Key, child.Value));
                    }
                    version = dirVersion;
                }
                else
                    version = new ElementVersion(branch, int.Parse(versionPath[versionPath.Length - 1]));
                _cleartool.GetVersionDetails(version);
                branch.Versions.Add(version);
            }
            Logger.TraceData(TraceEventType.Stop | TraceEventType.Verbose, (int)TraceId.ReadCleartool, "stop reading element", elementName);
        }

        public void Dispose()
        {
            _cleartool.Dispose();
        }
    }
}
