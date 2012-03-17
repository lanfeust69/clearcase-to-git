using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Text.RegularExpressions;

namespace GitImporter
{
    public class Cleartool : IDisposable
    {
        private const string _cleartool = "DummyCleartool.exe";

        public static TraceSource Logger = Program.Logger;

        private Process _process;
        private Thread _thread;
        private ManualResetEventSlim _cleartoolAvailable = new ManualResetEventSlim();

        private Regex _fileVersionRegex = new Regex("^version\\s+(\\S+)\\@\\@");
        private Regex _directoryVersionRegex = new Regex("^directory version\\s+(\\S+)\\@\\@");
        private Regex _symlinkRegex = new Regex("^symbolic link\\s+(\\S+ --> \\S+)\\@\\@");

        private List<string> _currentOutput = new List<string>();

        public Cleartool()
        {
            var startInfo = new ProcessStartInfo(_cleartool)
                            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
            _process = new Process { StartInfo = startInfo };
            _process.Start();
            _thread = new Thread(ReadOutput) { IsBackground = true };
            _thread.Start();
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
                        if (currentString == "cleartool> ")
                        {
                            _cleartoolAvailable.Set();
                            currentString = "";
                        }
                        break;
                }
            }
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
            ExecuteCommand("cd " + dir);
        }

        public string Pwd()
        {
            return ExecuteCommand("pwd")[0];
        }

        public List<string> Lsvtree(string element)
        {
            return ExecuteCommand("lsvtree " + element);
        }

        /// <summary>
        /// List content of a directory (possibly with a version-extended path)
        /// </summary>
        /// <param name="element">the directory to list</param>
        /// <returns>a triplet (directories, files, symlinks)</returns>
        public Tuple<List<string>, List<string>, List<string>> Ls(string element)
        {
            var dirs = new List<string>();
            var files = new List<string>();
            var symlinks = new List<string>();
            foreach (var line in ExecuteCommand("ls -l " + element))
            {
                Match match;
                if ((match = _directoryVersionRegex.Match(line)).Success)
                    dirs.Add(match.Groups[1].Value);
                else if ((match = _fileVersionRegex.Match(line)).Success)
                    files.Add(match.Groups[1].Value);
                if ((match = _symlinkRegex.Match(line)).Success)
                    symlinks.Add(match.Groups[1].Value);
            }
            return new Tuple<List<string>, List<string>, List<string>>(dirs, files, symlinks);
        }

        public string Oid(string element)
        {
            if (!element.EndsWith("@@"))
                element += "@@";
            return ExecuteCommand("desc -fmt %On " + element)[0];
        }

        public string Get(string element)
        {
            string tmp = Path.GetTempFileName();
            Console.WriteLine("Trying to write to " + tmp);
            ExecuteCommand("get -to " + tmp + " " + element);
            return tmp;
        }

        public void Dispose()
        {
            _process.StandardInput.WriteLine("quit");
            _thread.Join();
            _process.Close();
        }
    }
}
