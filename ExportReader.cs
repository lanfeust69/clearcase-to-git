using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.Diagnostics;

namespace GitImporter
{
    class ExportReader
    {
        public static TraceSource Logger = Program.Logger;

        private readonly DateTime _epoch = new DateTime(1970, 1, 1);

        private Regex _elementNameRegex = new Regex(@"^Name \d+:(.*)");
        private Regex _versionIdRegex = new Regex(@"^VersionId \d+:\\(.*)\\(\d+)");
        private Regex _userRegex = new Regex(@"^EventUser \d+:(.*)");
        private Regex _timeRegex = new Regex(@"^EventTime (\d+)");
        private Regex _commentRegex = new Regex(@"^Comment (\d+):(.*)");
        private Regex _labelRegex = new Regex(@"^Label \d+:(.*)");
        private Regex _subBranchRegex = new Regex(@"^SubBranch \d+:(.*)");

        public Dictionary<string, Dictionary<string, ElementBranch>> Elements { get; private set; }

        public ExportReader()
        {
            Elements = new Dictionary<string, Dictionary<string, ElementBranch>>();
        }
        
        public void ReadFile(string file)
        {
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadExport, "Start reading file", file);
            TextReader reader = new StreamReader(file);
            string line;
            string currentElement = null;
            Dictionary<string, ElementBranch> currentBranches = null;
            ElementBranch currentBranch = null;
            ElementVersion currentVersion = null;
            Match match;
            int lineNb = 0;
            int missingCommentChars = 0;
            string currentComment = null;
            // hack around end of lines
            string eol;
            while ((line = ReadLine(reader, out eol)) != null)
            {
                lineNb++;
                if (missingCommentChars > 0)
                {
                    currentComment += line;
                    missingCommentChars -= line.Length;
                    if (missingCommentChars < 0)
                        throw new Exception(file + ", line " + lineNb + " : Unexpected comment length");
                    if (missingCommentChars > 0)
                    {
                        currentComment += eol;
                        missingCommentChars -= eol.Length;
                    }
                    if (missingCommentChars == 0)
                    {
                        currentVersion.Comment = currentComment;
                        currentComment = null;
                    }
                    continue;
                }
                if (line == "ELEMENT_BEGIN")
                {
                    currentElement = null;
                    currentBranch = null;
                    currentBranches = null;
                    currentVersion = null;
                    continue;
                }
                if (currentElement == null && (match = _elementNameRegex.Match(line)).Success)
                {
                    currentElement = match.Groups[1].Value;
                    currentBranches = new Dictionary<string, ElementBranch>();
                    Elements[currentElement] = currentBranches;
                    Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.ReadExport, "start reading element", currentElement);
                    continue;
                }
                if (line == "ELEMENT_END")
                {
                    Logger.TraceData(TraceEventType.Stop | TraceEventType.Verbose, (int)TraceId.ReadExport, "stop reading element", currentElement);
                    continue;
                }

                if (line == "VERSION_BEGIN" || line == "VERSION_END")
                {
                    currentVersion = null;
                    continue;
                }
                if (currentElement != null && currentVersion == null && (match = _versionIdRegex.Match(line)).Success)
                {
                    string[] branchPath = match.Groups[1].Value.Split('\\');
                    string branchName = branchPath[branchPath.Length - 1];
                    if (currentBranch == null || (currentBranch.BranchName != branchName && !currentBranches.TryGetValue(branchName, out currentBranch)))
                    {
                        if (branchName != "main")
                            throw new Exception(file + ", line " + lineNb + " : Unexpected branch " + branchName);
                        currentBranch = new ElementBranch { BranchName = branchName, ElementName = currentElement };
                        currentBranches[branchName] = currentBranch;
                    }
                    currentVersion = new ElementVersion { ElementName = currentElement, Branch = currentBranch, VersionNumber = int.Parse(match.Groups[2].Value) };
                    currentBranch.Versions.Add(currentVersion);
                    Logger.TraceData(TraceEventType.Verbose, (int)TraceId.ReadExport, "creating version", currentVersion);
                    continue;
                }
                if (currentVersion != null && (match = _userRegex.Match(line)).Success)
                {
                    currentVersion.Author = match.Groups[1].Value;
                    continue;
                }
                if (currentVersion != null && (match = _timeRegex.Match(line)).Success)
                {
                    currentVersion.Date = _epoch.AddSeconds(long.Parse(match.Groups[1].Value));
                    continue;
                }
                if (currentVersion != null && (match = _labelRegex.Match(line)).Success)
                {
                    currentVersion.Labels.Add(match.Groups[1].Value);
                    continue;
                }
                if (currentVersion != null && (match = _commentRegex.Match(line)).Success)
                {
                    currentComment = match.Groups[2].Value;
                    missingCommentChars = int.Parse(match.Groups[1].Value) - currentComment.Length;
                    if (missingCommentChars > 0)
                    {
                        currentComment += eol;
                        missingCommentChars -= eol.Length;
                    }
                    if (missingCommentChars == 0 && currentComment.Length > 0)
                    {
                        currentVersion.Comment = currentComment;
                        currentComment = null;
                    }
                    continue;
                }
                if (currentVersion != null && (match = _subBranchRegex.Match(line)).Success)
                {
                    string branchName = match.Groups[1].Value;
                    if (currentBranches.ContainsKey(branchName))
                        throw new Exception(file + ", line " + lineNb + " : Duplicated branch " + branchName);
                    currentBranches[branchName] = new ElementBranch { ElementName = currentElement, BranchName = branchName, BranchingPoint = currentVersion };
                    continue;
                }
            }
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ReadExport, "Stop reading file", file);
        }

        private string ReadLine(TextReader reader, out string eol)
        {
            int c;
            var sb = new StringBuilder();
            while ((c = reader.Read()) != -1 && c != '\r' && c != '\n')
                sb.Append((char)c);
            if (c == -1)
                eol = null;
            else if (c == '\r')
            {
                c = reader.Peek();
                if (c != '\n')
                {
                    Logger.TraceEvent(TraceEventType.Warning, (int)TraceId.ReadExport, "Unexpected CR not followed by NL");
                    eol = "\r";
                }
                else
                {
                    reader.Read();
                    eol = "\r\n";
                }
            }
            else
                eol = "\n";
            if (sb.Length == 0 && eol == null)
                return null;
            return sb.ToString();
        }
    }
}
