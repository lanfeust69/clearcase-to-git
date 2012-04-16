using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Text.RegularExpressions;

namespace GitImporter
{
    public class Cleartool : IDisposable
    {
        private const string _cleartool = "cleartool_tty.exe";

        public static TraceSource Logger = Program.Logger;

        private readonly Process _process;
        private readonly Thread _outputThread;
        private readonly Thread _errorThread;
        private readonly ManualResetEventSlim _cleartoolAvailable = new ManualResetEventSlim();

        private readonly Regex _directoryEntryRegex = new Regex("^===> name: \"([^\"]+)\"");
        private readonly Regex _oidRegex = new Regex(@"cataloged oid: (\S+) \(mtype \d+\)");
        private readonly Regex _symlinkRegex = new Regex("^.+ --> (.+)$");
        private readonly Regex _mergeRegex = new Regex(@"^(""Merge@\d+@[^""]+"" (<-|->) ""[^""]+\\([^\\]+)\\(\d+)"" )+$");

        private readonly Regex _separator = new Regex("~#~");

        private List<string> _currentOutput = new List<string>();
        private string _lastError;
        private const int _nbRetry = 5;

        public Cleartool()
        {
            var startInfo = new ProcessStartInfo(_cleartool)
                            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            _process = new Process { StartInfo = startInfo };
            _process.Start();
            _outputThread = new Thread(ReadOutput) { IsBackground = true };
            _outputThread.Start();
            _errorThread = new Thread(ReadError) { IsBackground = true };
            _errorThread.Start();
            _cleartoolAvailable.Wait();
        }

        void ReadOutput()
        {
            int c;
            string currentString = "";
            const string prompt = "cleartool> ";
            int promptLength = prompt.Length;
            int currentIndexInPrompt = 0;
            while ((c = _process.StandardOutput.Read()) != -1)
            {
                switch ((char)c)
                {
                    case '\r':
                    case '\n':
                        if (!string.IsNullOrWhiteSpace(currentString))
                            _currentOutput.Add(currentString);
                        currentString = "";
                        break;
                    default:
                        currentString += (char)c;
                        if (prompt[currentIndexInPrompt] == (char)c)
                        {
                            currentIndexInPrompt++;
                            if (currentIndexInPrompt == promptLength)
                            {
                                string last = currentString.Substring(0, currentString.Length - promptLength);
                                if (last.Length > 0)
                                    _currentOutput.Add(last);
                                currentString = "";
                                currentIndexInPrompt = 0;
                                _cleartoolAvailable.Set();
                            }
                        }
                        else
                            // fortunately, there is only one 'c' in the prompt
                            currentIndexInPrompt = (char)c == prompt[0] ? 1 : 0;
                        break;
                }
            }
        }

        void ReadError()
        {
            string error;
            while ((error = _process.StandardError.ReadLine()) != null)
            {
                _lastError = error;
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.Cleartool, error);
            }
        }

        private List<string> ExecuteCommand(string cmd)
        {
            for (int i = 0; i < _nbRetry; i++)
            {
                Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.Cleartool, "Start executing cleartool command", cmd);
                _cleartoolAvailable.Reset();
                _lastError = null;
                _currentOutput = new List<string>();
                _process.StandardInput.WriteLine(cmd);
                _cleartoolAvailable.Wait();
                Logger.TraceData(TraceEventType.Stop | TraceEventType.Verbose, (int)TraceId.Cleartool, "Stop executing cleartool command", cmd);
                if (_lastError != null)
                {
                    bool lastTry = i == _nbRetry - 1;
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.Cleartool, "Cleartool command failed" + (!lastTry ? ", retrying" : ""), cmd);
                    if (!lastTry)
                        Thread.Sleep(2000);
                }
                else
                {
                    if (i > 0)
                        Logger.TraceData(TraceEventType.Information, (int)TraceId.Cleartool, "Cleartool command succeeded on retry #" + i, cmd);
                    var result = _currentOutput;
                    return result;
                }
            }
            Logger.TraceData(TraceEventType.Warning, (int)TraceId.Cleartool, "Cleartool command failed " + _nbRetry + " times, aborting", cmd);
            return new List<string>();
        }

        public void Cd(string dir)
        {
            ExecuteCommand("cd \"" + dir + "\"");
        }

        public string Pwd()
        {
            return ExecuteCommand("pwd")[0];
        }

        public List<string> Lsvtree(string element)
        {
            return ExecuteCommand("lsvtree -short -all -obsolete \"" + element + "\"").Select(v => v.Substring(v.LastIndexOf("@@") + 2)).ToList();
        }

        /// <summary>
        /// List content of a directory (possibly with a version-extended path),
        /// as a dictionary &lt;name as it appears in this version, oid of the element&gt;
        /// Symbolic links are stored as a string with the SYMLINK prefix
        /// </summary>
        public Dictionary<string, string> Ls(string element)
        {
            var result = new Dictionary<string, string>();
            string name = null, oid = null;
            foreach (var line in ExecuteCommand("ls -dump \"" + element + "\""))
            {
                Match match;
                if ((match = _directoryEntryRegex.Match(line)).Success)
                {
                    if (name != null && oid != null)
                        result[name] = oid;
                    name = match.Groups[1].Value;
                    oid = null;
                }
                else if ((match = _oidRegex.Match(line)).Success)
                    oid = match.Groups[1].Value;
                else if ((match = _symlinkRegex.Match(line)).Success)
                    oid = SymLinkElement.SYMLINK + match.Groups[1].Value;
            }
            if (name != null && oid != null)
                result[name] = oid;
            return result;
        }

        public string GetOid(string element)
        {
            bool isDir;
            return GetOid(element, out isDir);
        }

        public string GetOid(string element, out bool isDir)
        {
            isDir = false;
            if (!element.EndsWith("@@"))
                element += "@@";
            var result = ExecuteCommand("desc -fmt \"%On" + _separator + "%m\" \"" + element + "\"");
            if (result.Count == 0)
                return null;
            string[] parts = _separator.Split(result[0]);
            isDir = parts[1] == "directory element";
            return parts[0];
        }

        public string GetPredecessor(string version)
        {
            return ExecuteCommand("desc -pred -s \"" + version + "\"").FirstOrDefault();
        }

        public void GetVersionDetails(ElementVersion version, out List<Tuple<string, int>> mergesTo, out List<Tuple<string, int>> mergesFrom)
        {
            bool isDir = version.Element.IsDirectory;
            // not interested in directory merges
            string format = "%Fu" + _separator + "%u" + _separator + "%Nd" + _separator + "%Nc" + _separator + "%Nl" +
                (isDir ? "" : _separator + "%[hlink:Merge]p");
            // string.Join to handle multi-line comments
            string raw = string.Join("\r\n", ExecuteCommand("desc -fmt \"" + format + "\" \"" + version + "\""));
            string[] parts = _separator.Split(raw);
            version.AuthorName = string.Intern(parts[0]);
            version.AuthorLogin = string.Intern(parts[1]);
            version.Date = DateTime.ParseExact(parts[2], "yyyyMMdd.HHmmss", null).ToUniversalTime();
            version.Comment = string.Intern(parts[3]);
            foreach (string label in parts[4].Split(' '))
                if (!string.IsNullOrWhiteSpace(label))
                    version.Labels.Add(string.Intern(label));
            mergesTo = mergesFrom = null;
            if (isDir || string.IsNullOrEmpty(parts[5]))
                return;

            Match match = _mergeRegex.Match(parts[5]);
            if (!match.Success)
            {
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.Cleartool, "Failed to parse merge data '" + parts[5] + "'");
                return;
            }
            mergesTo = new List<Tuple<string, int>>();
            mergesFrom = new List<Tuple<string, int>>();
            int count = match.Groups[1].Captures.Count;
            for (int i = 0; i < count; i++)
                (match.Groups[2].Captures[i].Value == "->" ? mergesTo : mergesFrom)
                    .Add(new Tuple<string, int>(match.Groups[3].Captures[i].Value, int.Parse(match.Groups[4].Captures[i].Value)));
        }

        public string Get(string element)
        {
            string tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            ExecuteCommand("get -to " + tmp + " \"" + element + "\"");
            return tmp;
        }

        public void Dispose()
        {
            _process.StandardInput.WriteLine("quit");
            _outputThread.Join();
            _errorThread.Join();
            _process.Close();
        }
    }
}
