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
        private readonly Regex _oidRegex = new Regex("cataloged oid: (\\S+) \\(mtype \\d+\\)");
        private readonly Regex _symlinkRegex = new Regex("^.+ --> (.+)$");

        private List<string> _currentOutput = new List<string>();

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
                        if (currentString.EndsWith("cleartool> "))
                        {
                            string last = currentString.Substring(0, currentString.Length - "cleartool> ".Length);
                            if (last.Length > 0)
                                _currentOutput.Add(last);
                            currentString = "";
                            _cleartoolAvailable.Set();
                        }
                        break;
                }
            }
        }

        void ReadError()
        {
            string error;
            while ((error = _process.StandardError.ReadLine()) != null)
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.Cleartool, error);
        }

        private List<string> ExecuteCommand(string cmd)
        {
            Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.Cleartool, "Start executing cleartool command", cmd);
            _cleartoolAvailable.Reset();
            _process.StandardInput.WriteLine(cmd);
            _cleartoolAvailable.Wait();
            Logger.TraceData(TraceEventType.Stop | TraceEventType.Verbose, (int)TraceId.Cleartool, "Stop executing cleartool command", cmd);
            var result = _currentOutput;
            _currentOutput = new List<string>();
            return result;
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
        /// We try to resolve symbolic links immediately, but not in a very robust way
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
                {
                    // TODO : handle symlinks
                }
            }
            if (name != null && oid != null)
                result[name] = oid;
            return result;
        }

        public string GetOid(string element)
        {
            if (!element.EndsWith("@@"))
                element += "@@";
            var result = ExecuteCommand("desc -fmt %On \"" + element + "\"");
            return result.Count > 0 ? result[0] : null;
        }

        public void GetVersionDetails(ElementVersion version)
        {
            // string.Join to handle multi-line comments
            string raw = string.Join("\r\n", ExecuteCommand("desc -fmt %Fu§%u§%Nd§%Nc§%Nl \"" + version + "\""));
            string[] parts = raw.Split('§');
            version.AuthorName = parts[0];
            version.AuthorLogin = parts[1];
            version.Date = DateTime.ParseExact(parts[2], "yyyyMMdd.HHmmss", null).ToUniversalTime();
            version.Comment = parts[3];
            foreach (string label in parts[4].Split(' '))
                if (!string.IsNullOrWhiteSpace(label))
                    version.Labels.Add(label);
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
