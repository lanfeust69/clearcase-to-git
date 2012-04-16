using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace GitImporter
{
    public class GitWriter : IDisposable
    {
        public static TraceSource Logger = Program.Logger;

        private static readonly DateTime _epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly StreamWriter _writer = new StreamWriter(Console.OpenStandardOutput());
        private readonly Cleartool _cleartool;

        private readonly bool _doNotIncludeFileContent;
        private readonly string _gitIgnoreFile;
        private bool _gitIgnoreAdded;

        public GitWriter(string clearcaseRoot, bool doNotIncludeFileContent, string gitIgnoreFile)
        {
            _doNotIncludeFileContent = doNotIncludeFileContent;
            _gitIgnoreFile = gitIgnoreFile;
            if (_doNotIncludeFileContent)
                return;
            _cleartool = new Cleartool();
            _cleartool.Cd(clearcaseRoot);
        }

        public GitWriter(string clearcaseRoot, bool doNotIncludeFileContent)
            : this(clearcaseRoot, doNotIncludeFileContent, null)
        {
        }

        public void WriteChangeSets(IList<ChangeSet> changeSets)
        {
            int total = changeSets.Count;
            int n = 0;
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ApplyChangeSet, "Start writing " + total + " change sets");

            // how frequently to report progress
            int frequency;
            var queue = new Queue<int>(new[] { 1, 2, 5 });
            while (total / (frequency = queue.Dequeue()) > 1000)
                queue.Enqueue(frequency * 10);

            _gitIgnoreAdded = string.IsNullOrEmpty(_gitIgnoreFile); // already "added" if not specified
            foreach (var changeSet in changeSets)
            {
                n++;
                if (n % frequency == 0)
                    _writer.Write("progress Writing change set " + n + " of " + total + "\n\n");

                Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.ApplyChangeSet, "Start writing change set", n);
                WriteChangeSet(changeSet);
                Logger.TraceData(TraceEventType.Stop | TraceEventType.Verbose, (int)TraceId.ApplyChangeSet, "Stop writing change set", n);
            }

            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ApplyChangeSet, "Stop writing " + total + " change sets");
        }

        private void WriteChangeSet(ChangeSet changeSet)
        {
            if (changeSet.IsEmpty)
                return;

            string branchName = changeSet.Branch == "main" ? "master" : changeSet.Branch;
            _writer.Write("commit refs/heads/" + branchName + "\n");
            _writer.Write("mark :" + changeSet.Id + "\n");
            _writer.Write("committer " + changeSet.AuthorName + " <" + changeSet.AuthorLogin + "> " + (changeSet.StartTime - _epoch).TotalSeconds + " +0200\n");
            string comment = changeSet.GetComment();
            byte[] encoded = Encoding.UTF8.GetBytes(comment);
            _writer.Write("data " + encoded.Length + "\n");
            _writer.Flush();
            _writer.BaseStream.Write(encoded, 0, encoded.Length);
            _writer.Write("\n");
            if (changeSet.BranchingPoint != null)
                _writer.Write("from :" + changeSet.BranchingPoint.Id + "\n");
            foreach (var merge in changeSet.Merges)
                _writer.Write("merge :" + merge.Id + "\n");

            if (!_gitIgnoreAdded && branchName == "master")
            {
                _gitIgnoreAdded = true;
                var fileInfo = new FileInfo(_gitIgnoreFile);
                if (fileInfo.Exists)
                {
                    _writer.Write("M 644 inline .gitignore\ndata " + fileInfo.Length + "\n");
                    // Flush() before using BaseStream directly
                    _writer.Flush();
                    using (var s = new FileStream(_gitIgnoreFile, FileMode.Open, FileAccess.Read))
                        s.CopyTo(_writer.BaseStream);
                }
            }

            // order is significant : we must Rename and Copy files before (maybe) deleting their directory
            foreach (var pair in changeSet.Renamed)
                _writer.Write("R \"" + pair.Item1 + "\" \"" + pair.Item2 + "\"\n");
            foreach (var pair in changeSet.Copied)
                _writer.Write("C \"" + pair.Item1 + "\" \"" + pair.Item2 + "\"\n");
            foreach (var removed in changeSet.Removed)
                _writer.Write("D " + removed + "\n");

            foreach (var namedVersion in changeSet.Versions)
            {
                if (namedVersion.Version is DirectoryVersion || namedVersion.Names.Count == 0)
                    continue;

                bool isEmptyFile = namedVersion.Version.VersionNumber == 0 && namedVersion.Version.Branch.BranchName == "main";

                if (_doNotIncludeFileContent || isEmptyFile)
                {
                    foreach (string name in namedVersion.Names)
                        if (isEmptyFile)
                            _writer.Write("M 644 inline " + name + "\ndata 0\n\n");
                        else
                            _writer.Write("M 644 inline " + name + "\ndata <<EOF\n" + namedVersion.Version + "\nEOF\n\n");
                    continue;
                }

                string fileName = _cleartool.Get(namedVersion.Version.ToString());
                var fileInfo = new FileInfo(fileName);
                if (!fileInfo.Exists)
                {
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.ApplyChangeSet, "Version " + namedVersion + " could not be read from clearcase");
                    // still create a file for later delete or rename
                    foreach (string name in namedVersion.Names)
                    {
                        _writer.Write("M 644 inline " + name + "\ndata <<EOF\n");
                        _writer.Write("// clearcase error while retrieving " + namedVersion + "\nEOF\n\n");
                    }
                    continue;
                }
                foreach (string name in namedVersion.Names)
                {
                    _writer.Write("M 644 inline " + name + "\ndata " + fileInfo.Length + "\n");
                    // Flush() before using BaseStream directly
                    _writer.Flush();
                    using (var s = new FileStream(fileName, FileMode.Open, FileAccess.Read))
                        s.CopyTo(_writer.BaseStream);
                }
                // clearcase always create as ReadOnly
                fileInfo.IsReadOnly = false;
                fileInfo.Delete();
                _writer.Write("\n");
            }

            foreach (var label in changeSet.Labels)
            {
                _writer.Write("tag " + label + "\n");
                _writer.Write("from :" + changeSet.Id + "\n");
                _writer.Write("tagger Unknown <unknown> " + (changeSet.StartTime - _epoch).TotalSeconds + " +0200\n");
                _writer.Write("data 0\n\n");
            }
        }

        public void WriteFile(string fileName)
        {
            _writer.Flush();
            using (var s = new FileStream(fileName, FileMode.Open, FileAccess.Read))
                s.CopyTo(_writer.BaseStream);
        }

        public void Dispose()
        {
            _writer.Dispose();
            if (_cleartool != null)
                _cleartool.Dispose();
        }
    }
}
