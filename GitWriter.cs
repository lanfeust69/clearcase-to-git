using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GitImporter
{
    public class GitWriter : IDisposable
    {
        public class PreWritingHook
        {
            public Regex Target { get; private set; }
            public Action<string> Hook { get; private set; }

            public PreWritingHook(Regex target, Action<string> hook)
            {
                Target = target;
                Hook = hook;
            }
        }

        public class PostWritingHook
        {
            public Regex Target { get; private set; }
            public Action<string, StreamWriter> Hook { get; private set; }

            public PostWritingHook(Regex target, Action<string, StreamWriter> hook)
            {
                Target = target;
                Hook = hook;
            }
        }

        public static TraceSource Logger = Program.Logger;

        private static readonly DateTime _epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly StreamWriter _writer = new StreamWriter(Console.OpenStandardOutput());
        private readonly Cleartool _cleartool;

        private readonly bool _doNotIncludeFileContent;
        private bool _initialFilesAdded;
        private bool _isIncremental;
        private readonly HashSet<string> _startedBranches = new HashSet<string>();

        public List<Tuple<string, string>> InitialFiles { get; private set; }

        public List<PreWritingHook> PreWritingHooks { get; private set; }
        public List<PostWritingHook> PostWritingHooks { get; private set; }

        public GitWriter(string clearcaseRoot, bool doNotIncludeFileContent, IEnumerable<string> labels)
        {
            _doNotIncludeFileContent = doNotIncludeFileContent;
            InitialFiles = new List<Tuple<string, string>>();
            PreWritingHooks = new List<PreWritingHook>();
            PostWritingHooks = new List<PostWritingHook>();

            if (_doNotIncludeFileContent)
                return;
            _cleartool = new Cleartool(clearcaseRoot, new LabelFilter(labels));
        }

        public void WriteChangeSets(IList<ChangeSet> changeSets)
        {
            int total = changeSets.Count;
            int n = 0;
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ApplyChangeSet, "Start writing " + total + " change sets");

            _isIncremental = changeSets.Count > 0 && changeSets[0].Id > 1;
            int checkpointFrequency = ComputeFrequency(total, 10);
            int reportFrequency = ComputeFrequency(total, 1000);

            _initialFilesAdded = InitialFiles.Count == 0; // already "added" if not specified
            foreach (var changeSet in changeSets)
            {
                n++;
                if (!_isIncremental && n % checkpointFrequency == 0)
                    _writer.Write("checkpoint\n\n");
                if (n % reportFrequency == 0)
                    _writer.Write("progress Writing change set " + n + " of " + total + "\n\n");

                Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.ApplyChangeSet, "Start writing change set", n);
                WriteChangeSet(changeSet);
                Logger.TraceData(TraceEventType.Stop | TraceEventType.Verbose, (int)TraceId.ApplyChangeSet, "Stop writing change set", n);
            }

            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ApplyChangeSet, "Stop writing " + total + " change sets");
        }

        private static int ComputeFrequency(int total, int target)
        {
            int frequency;
            var queue = new Queue<int>(new[] { 1, 2, 5 });
            while (total / (frequency = queue.Dequeue()) > target)
                queue.Enqueue(frequency * 10);
            return frequency;
        }

        private void WriteChangeSet(ChangeSet changeSet)
        {
            if (changeSet.IsEmpty)
            {
                Logger.TraceData(TraceEventType.Information, (int)TraceId.ApplyChangeSet, "Skipped empty ChangeSet " + changeSet);
                return;
            }

            string branchName = changeSet.Branch == "main" ? "master" : changeSet.Branch;
            _writer.Write("commit refs/heads/" + branchName + "\n");
            _writer.Write("mark :" + changeSet.Id + "\n");
            _writer.Write("committer " + changeSet.AuthorName + " <" + changeSet.AuthorLogin + "> " + (changeSet.StartTime - _epoch).TotalSeconds + " +0200\n");
            InlineString(changeSet.GetComment());
            if (changeSet.BranchingPoint != null)
            {
                _writer.Write("from :" + changeSet.BranchingPoint.Id + "\n");
                _startedBranches.Add(branchName);
            }
            else if (_isIncremental && !_startedBranches.Contains(branchName))
            {
                _writer.Write("from refs/heads/" + branchName + "^0\n");
                _startedBranches.Add(branchName);
            }
            foreach (var merge in changeSet.Merges)
                _writer.Write("merge :" + merge.Id + "\n");

            if (!_initialFilesAdded && branchName == "master")
            {
                _initialFilesAdded = true;
                foreach (var initialFile in InitialFiles)
                {
                    var fileInfo = new FileInfo(initialFile.Item2);
                    if (fileInfo.Exists)
                    {
                        _writer.Write("M 644 inline " + initialFile.Item1 + "\ndata " + fileInfo.Length + "\n");
                        // Flush() before using BaseStream directly
                        _writer.Flush();
                        using (var s = new FileStream(initialFile.Item2, FileMode.Open, FileAccess.Read))
                            s.CopyTo(_writer.BaseStream);
                    }
                }
            }

            // order is significant : we must Rename and Copy files before (maybe) deleting their directory
            foreach (var pair in changeSet.Renamed)
                _writer.Write("R \"" + RemoveDotRoot(pair.Item1) + "\" \"" + RemoveDotRoot(pair.Item2) + "\"\n");
            foreach (var pair in changeSet.Copied)
                _writer.Write("C \"" + RemoveDotRoot(pair.Item1) + "\" \"" + RemoveDotRoot(pair.Item2) + "\"\n");
            foreach (var removed in changeSet.Removed)
                _writer.Write("D " + RemoveDotRoot(removed) + "\n");

            foreach (var symLink in changeSet.SymLinks)
            {
                _writer.Write("M 120000 inline " + RemoveDotRoot(symLink.Item1) + "\n");
                InlineString(RemoveDotRoot(symLink.Item2));
            }

            foreach (var namedVersion in changeSet.Versions)
            {
                if (namedVersion.Version is DirectoryVersion || namedVersion.Names.Count == 0)
                    continue;

                bool isEmptyFile = namedVersion.Version.VersionNumber == 0 && namedVersion.Version.Branch.BranchName == "main";

                if (_doNotIncludeFileContent || isEmptyFile)
                {
                    foreach (string name in namedVersion.Names.Select(RemoveDotRoot))
                        if (isEmptyFile)
                            _writer.Write("M 644 inline " + name + "\ndata 0\n\n");
                        else
                        {
                            // don't use InlineString here, so that /FetchFileContent is easy to implement
                            _writer.Write("M 644 inline " + name + "\ndata <<EOF\n" + namedVersion.Version + "#" + namedVersion.Version.Element.Oid + "\nEOF\n\n");
                            // also include name in a comment for hooks in /FetchFileContent
                            _writer.Write("#" + name + "\n");
                        }
                    continue;
                }

                InlineClearcaseFileVersion(namedVersion.Version.Element.Name, namedVersion.Version.Element.Oid, namedVersion.Version.VersionPath, namedVersion.Names.Select(RemoveDotRoot), true);
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

        private static string RemoveDotRoot(string path)
        {
            return path.StartsWith("./") ? path.Substring(2) : path;
        }

        private void InlineClearcaseFileVersion(string elementPath, string elementOid, string version, IEnumerable<string> names, bool writeNames)
        {
            string fullName = elementPath + "@@" + version;
            string fileName = _cleartool.Get(fullName);
            var fileInfo = new FileInfo(fileName);
            if (!fileInfo.Exists)
            {
                // in incremental import, elements may have been moved, without a new version, we try to use the oid
                // (we don't always do that to avoid unecessary calls to cleartool)
                string newElementName = _cleartool.GetElement(elementOid);
                if (!string.IsNullOrEmpty(newElementName))
                {
                    // GetElement returns a "real" element name, ie ending with "@@"
                    fullName = newElementName + version;
                    fileName = _cleartool.Get(fullName);
                    fileInfo = new FileInfo(fileName);
                }
                else
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.ApplyChangeSet, "Element with oid " + elementOid + " could not be found in clearcase");
            }
            if (!fileInfo.Exists)
            {
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.ApplyChangeSet, "Version " + fullName + " could not be read from clearcase");
                // still create a file for later delete or rename
                foreach (string name in names)
                {
                    if (writeNames)
                        _writer.Write("M 644 inline " + name + "\n");
                    InlineString("// clearcase error while retrieving " + fullName);
                }
                return;
            }
            // clearcase always create as ReadOnly
            fileInfo.IsReadOnly = false;
            foreach (string name in names)
            {
                foreach (var hook in PreWritingHooks)
                    if (hook.Target.IsMatch(name))
                        hook.Hook(fileName);

                if (writeNames)
                    _writer.Write("M 644 inline " + name + "\n");
                _writer.Write("data " + fileInfo.Length + "\n");
                // Flush() before using BaseStream directly
                _writer.Flush();
                using (var s = new FileStream(fileName, FileMode.Open, FileAccess.Read))
                    s.CopyTo(_writer.BaseStream);
                _writer.Write("\n");
                foreach (var hook in PostWritingHooks)
                    if (hook.Target.IsMatch(name))
                        hook.Hook(fileName, _writer);
            }
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
                    if (string.IsNullOrEmpty(versionToFetch))
                        throw new Exception("Error line " + lineNb + " : expecting version path, reading empty line");
                    string eof = s.ReadLine();
                    if (eof != "EOF")
                        throw new Exception("Error line " + lineNb + " : expecting 'EOF', reading '" + eof + "'");
                    eof = s.ReadLine();
                    if (eof != "")
                        throw new Exception("Error line " + lineNb + " : expecting blank line, reading '" + eof + "'");
                    var name = s.ReadLine();
                    if (name == null || !name.StartsWith("#"))
                        throw new Exception("Error line " + lineNb + " : expecting comment with file name, reading '" + name + "'");
                    lineNb += 5;
                    string elementOid = null;
                    var parts = versionToFetch.Split('#');
                    // backward compatibility : do not require oid
                    if (parts.Length == 2)
                    {
                        versionToFetch = parts[0];
                        elementOid = parts[1];
                    }
                    int pos = versionToFetch.LastIndexOf("@@");
                    string elementPath = versionToFetch.Substring(0, pos);
                    string versionPath = versionToFetch.Substring(pos + 2);
                    InlineClearcaseFileVersion(elementPath, elementOid, versionPath, new[] { name.Substring(1) }, false);
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
