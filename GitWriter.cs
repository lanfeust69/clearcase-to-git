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
            InlineString(changeSet.GetComment());
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

            foreach (var symLink in changeSet.SymLinks)
            {
                _writer.Write("M 120000 inline " + symLink.Item1 + "\n");
                InlineString(symLink.Item2);
            }

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
                            // don't use InlineString here, so that /FetchFileContent is easy to implement
                            _writer.Write("M 644 inline " + name + "\ndata <<EOF\n" + namedVersion.Version + "\nEOF\n\n");
                    continue;
                }

                InlineClearcaseFileVersion(namedVersion.Version.ToString(), namedVersion.Names);
            }

            foreach (var label in changeSet.Labels)
            {
                _writer.Write("tag " + label + "\n");
                _writer.Write("from :" + changeSet.Id + "\n");
                _writer.Write("tagger Unknown <unknown> " + (changeSet.StartTime - _epoch).TotalSeconds + " +0200\n");
                _writer.Write("data 0\n\n");
            }
        }

        private void InlineString(string data)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(data);
            _writer.Write("data " + encoded.Length + "\n");
            _writer.Flush();
            _writer.BaseStream.Write(encoded, 0, encoded.Length);
            _writer.Write("\n");
        }

        private void InlineClearcaseFileVersion(string version)
        {
            InlineClearcaseFileVersion(version, new string[] { null });
        }

        private void InlineClearcaseFileVersion(string version, IEnumerable<string> names)
        {
            string fileName = _cleartool.Get(version);
            var fileInfo = new FileInfo(fileName);
            if (!fileInfo.Exists)
            {
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.ApplyChangeSet, "Version " + version + " could not be read from clearcase");
                // still create a file for later delete or rename
                foreach (string name in names)
                {
                    if (!string.IsNullOrEmpty(name))
                        _writer.Write("M 644 inline " + name + "\n");
                    InlineString("// clearcase error while retrieving " + version);
                }
                return;
            }
            foreach (string name in names)
            {
                if (!string.IsNullOrEmpty(name))
                    _writer.Write("M 644 inline " + name + "\n");
                _writer.Write("data " + fileInfo.Length + "\n");
                // Flush() before using BaseStream directly
                _writer.Flush();
                using (var s = new FileStream(fileName, FileMode.Open, FileAccess.Read))
                    s.CopyTo(_writer.BaseStream);
                _writer.Write("\n");
            }
            // clearcase always create as ReadOnly
            fileInfo.IsReadOnly = false;
            fileInfo.Delete();
        }

        public void WriteFile(string fileName)
        {
            // we can't simply ReadLine() because of end of line discrepencies within comments
            int c;
            int lineNb = 0;
            var currentLine = new List<char>(1024);
            const string inlineFile = "data <<EOF\n";
            int index = 0;
            using (var s = new StreamReader(fileName))
                while ((c = s.Read()) != -1)
                {
                    if (index == -1)
                    {
                        _writer.Write((char)c);
                        if (c == '\n')
                        {
                            index = 0;
                            lineNb++;
                        }
                        continue;
                    }
                    if (c != inlineFile[index])
                    {
                        foreach (char c1 in currentLine)
                            _writer.Write(c1);
                        _writer.Write((char)c);
                        currentLine.Clear();
                        index = c == '\n' ? 0 : -1;
                        continue;
                    }
                    index++;
                    currentLine.Add((char)c);
                    if (index < inlineFile.Length)
                        continue;
                    // we just matched the whole "data <<EOF\n" line : next line is the version we should fetch
                    string versionToFetch = s.ReadLine();
                    string eof = s.ReadLine();
                    if (eof != "EOF")
                        throw new Exception("Error line " + lineNb + " : expecting 'EOF', reading '" + eof + "'");
                    eof = s.ReadLine();
                    if (eof != "")
                        throw new Exception("Error line " + lineNb + " : expecting blank line, reading '" + eof + "'");
                    lineNb += 4;
                    InlineClearcaseFileVersion(versionToFetch);
                    currentLine.Clear();
                    index = 0;
                }
        }

        public void Dispose()
        {
            _writer.Dispose();
            if (_cleartool != null)
                _cleartool.Dispose();
        }
    }
}
