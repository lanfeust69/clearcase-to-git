using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace GitImporter
{
    public class GitWriter : IDisposable
    {
        public static TraceSource Logger = Program.Logger;

        private static readonly DateTime _epoch = new DateTime(1970, 1, 1);

        private readonly StreamWriter _writer = new StreamWriter(Console.OpenStandardOutput());
        private readonly Cleartool _cleartool = new Cleartool();

        public void WriteChangeSets(IList<ChangeSet> changeSets)
        {
            int total = changeSets.Count;
            int n = 0;
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ApplyChangeSet, "Start writing " + total + " change sets");

            var startedBranches = new HashSet<string>();
            var elementNamesByBranch = new Dictionary<string, Dictionary<Element, string>>();
            foreach (var changeSet in changeSets)
            {
                n++;
                if (total < 100 || n % (total / 100) == 0)
                    _writer.Write("progress Writing change set " + n + " of " + total + "\n\n");
                    //Logger.TraceData(TraceEventType.Information, (int)TraceId.ApplyChangeSet, "Writing change set " + n + " of " + total);

                Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.ApplyChangeSet, "Start writing change set", n);
                Dictionary<Element, string> elementNames;
                bool isNewBranch = !startedBranches.Contains(changeSet.Branch);
                if (isNewBranch)
                {
                    // n is 1-based index because it is used as a mark id for git fast-import, that reserves id 0
                    if (n > 1)
                        // again wrong for multiple branches starting from the same commit
                        elementNames = new Dictionary<Element, string>(elementNamesByBranch[changeSets[n - 2].Branch]);
                    else
                        elementNames = new Dictionary<Element, string>();
                    elementNamesByBranch.Add(changeSet.Branch, elementNames);
                }
                else
                    elementNames = elementNamesByBranch[changeSet.Branch];
                WriteChangeSet(changeSet, n, isNewBranch, elementNames);
                startedBranches.Add(changeSet.Branch);
                Logger.TraceData(TraceEventType.Stop | TraceEventType.Verbose, (int)TraceId.ApplyChangeSet, "Stop writing change set", n);
            }

            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ApplyChangeSet, "Stop writing " + total + " change sets");
        }

        private void WriteChangeSet(ChangeSet changeSet, int commitNumber, bool isNewBranch, Dictionary<Element, string> elementNames)
        {
            string branchName = changeSet.Branch == "main" ? "master" : changeSet.Branch;
            _writer.Write("commit refs/heads/" + branchName + "\n");
            _writer.Write("mark :" + commitNumber + "\n");
            _writer.Write("committer " + changeSet.Author + " <" + changeSet.Author + "@sgcib.com> " + (changeSet.StartTime - _epoch).TotalSeconds + " +0200\n");
            string comment = changeSet.GetComment();
            _writer.Write("data " + comment.Length + "\n" + comment + "\n");
            if (isNewBranch && branchName != "master" && commitNumber > 1)
                // TODO : commitNumber - 1 is not always correct (for instance if two branches start from the same master commit)
                _writer.Write("from :" + (commitNumber - 1) + "\n");
            // handle remove and rename, and update elementNames
            ProcessDirectoryChanges(changeSet.Versions.OfType<DirectoryVersion>(), elementNames);

            foreach (var version in changeSet.Versions)
            {
                if (version is DirectoryVersion)
                    continue;
                string fileName = _cleartool.Get(version.ToString());
                var fileInfo = new FileInfo(fileName);
                _writer.Write("M 644 inline " + elementNames[version.Element] + "\ndata " + fileInfo.Length + "\n");
                // Flush() before using BaseStream directly
                _writer.Flush();
                using (var s = new FileStream(fileName, FileMode.Open))
                    s.CopyTo(_writer.BaseStream);
                File.Delete(fileName);
                _writer.Write("\n");
            }
        }

        private void ProcessDirectoryChanges(IEnumerable<DirectoryVersion> versions, Dictionary<Element, string> elementNames)
        {
            // first order from roots to leaves (because changes to roots also impact leaves)
            var unorderedVersions = new List<DirectoryVersion>(versions);
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
                    baseName = version.Element.Name;
                    elementNames.Add(version.Element, baseName);
                }
                baseName += "/";
                ComputeDiffWithPrevious(version, baseName, removedElements, addedElements);
            }

            // with all the removals and addition to account for "long distance" moves
            WriteDeletesAndRenames(removedElements, addedElements);

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

        private void WriteDeletesAndRenames(Dictionary<Element, string> removedElements, Dictionary<Element, string> addedElements)
        {
            foreach (var pair in removedElements)
            {
                string newName;
                if (addedElements.TryGetValue(pair.Key, out newName))
                    _writer.Write("R " + pair.Value + " " + newName + "\n");
                else
                    _writer.Write("D " + pair.Value + "\n");
            }
            // other addedElements are really new, and will be written with their first version as content
        }

        public void Dispose()
        {
            _writer.Dispose();
            _cleartool.Dispose();
        }
    }
}
