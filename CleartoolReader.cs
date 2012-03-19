using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace GitImporter
{
    public class CleartoolReader : IDisposable
    {
        public static TraceSource Logger = Program.Logger;

        private readonly Cleartool _cleartool = new Cleartool();

        public Dictionary<string, Element> FileElements { get; private set; }
        public Dictionary<string, Element> DirectoryElements { get; private set; }
        public Dictionary<string, Element> ElementsByOid { get; private set; }

        private List<Tuple<DirectoryVersion, string, string>> _fixups = new List<Tuple<DirectoryVersion,string,string>>();

        public CleartoolReader()
        {
            DirectoryElements = new Dictionary<string, Element>();
            ElementsByOid = new Dictionary<string, Element>();
        }

        public VobDB VobDB { get { return new VobDB(DirectoryElements, FileElements, ElementsByOid); } }

        public void Init(Dictionary<string, Element> elements)
        {
            FileElements = elements;
            foreach (var element in FileElements)
            {
                string oid = _cleartool.GetOid(element.Key);
                element.Value.Oid = oid;
                ElementsByOid[oid] = element.Value;
            }
        }

        public void Read(string directoriesFile, string elementsFile)
        {
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadCleartool, "start reading file elements", elementsFile);
            using (var directories = new StreamReader(elementsFile))
            {
                string line;
                while ((line = directories.ReadLine()) != null)
                    ReadElement(line, false);
            }
            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ReadCleartool, "stop reading file elements", elementsFile);
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadCleartool, "start reading directory elements", directoriesFile);
            using (var directories = new StreamReader(directoriesFile))
            {
                string line;
                while ((line = directories.ReadLine()) != null)
                    ReadElement(line, true);
            }
            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ReadCleartool, "stop reading directory elements", directoriesFile);
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadCleartool, "start fixups");
            foreach (var fixup in _fixups)
            {
                Element childElement;
                if (ElementsByOid.TryGetValue(fixup.Item3, out childElement))
                    fixup.Item1.Content.Add(new KeyValuePair<string, Element>(fixup.Item2, childElement));
            }
            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ReadCleartool, "stop fixups");
        }

        private void ReadElement(string elementName, bool isDir)
        {
            string oid = _cleartool.GetOid(elementName);
            if (ElementsByOid.ContainsKey(oid))
                return;
            Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.ReadCleartool,
                "start reading " + (isDir ? "directory" : "file") + " element", elementName);
            var element = new Element(elementName, isDir);
            element.Oid = oid;
            ElementsByOid[oid] = element;
            (isDir ? DirectoryElements : FileElements)[elementName] = element;
            foreach (string versionString in _cleartool.Lsvtree(elementName))
            {
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
                    foreach (var childName in res.Item1.Union(res.Item2))
                    {
                        string childOid = _cleartool.GetOid(dirVersion + "\\" + childName);
                        Element childElement;
                        if (ElementsByOid.TryGetValue(childOid, out childElement))
                            dirVersion.Content.Add(new KeyValuePair<string, Element>(childName, childElement));
                        else
                            _fixups.Add(new Tuple<DirectoryVersion, string, string>(dirVersion, childName, childOid));
                    }
                    foreach (var symlink in res.Item3)
                    {
                        string[] parts = symlink.Split(new[] { " --> " }, StringSplitOptions.None);
                        string childOid = _cleartool.GetOid(dirVersion + "\\" + parts[1]);
                        Element childElement;
                        if (ElementsByOid.TryGetValue(childOid, out childElement))
                            dirVersion.Content.Add(new KeyValuePair<string, Element>(parts[0], childElement));
                        else
                            _fixups.Add(new Tuple<DirectoryVersion, string, string>(dirVersion, parts[0], childOid));
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
