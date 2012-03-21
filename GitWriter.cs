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
            foreach (var changeSet in changeSets)
            {
                n++;
                if (total < 100 || n % (total / 100) == 0)
                    _writer.Write("progress Writing change set " + n + " of " + total + "\n\n");
                    //Logger.TraceData(TraceEventType.Information, (int)TraceId.ApplyChangeSet, "Writing change set " + n + " of " + total);

                Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.ApplyChangeSet, "Start writing change set", n);
                WriteChangeSet(changeSet, n, !startedBranches.Contains(changeSet.Branch));
                startedBranches.Add(changeSet.Branch);
                Logger.TraceData(TraceEventType.Stop | TraceEventType.Verbose, (int)TraceId.ApplyChangeSet, "Stop writing change set", n);
            }

            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ApplyChangeSet, "Stop writing " + total + " change sets");
        }

        private void WriteChangeSet(ChangeSet changeSet, int commitNumber, bool isNewBranch)
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
            foreach (var version in changeSet.Versions)
            {
                // TODO : handle directories
                if (version is DirectoryVersion)
                    continue;
                string fileName = _cleartool.Get(version.ToString());
                var fileInfo = new FileInfo(fileName);
                _writer.Write("M 644 inline " + version.Element.Name.Replace('\\', '/') + "\ndata " + fileInfo.Length + "\n");
                // Flush() before using BaseStream directly
                _writer.Flush();
                using (var s = new FileStream(fileName, FileMode.Open))
                    s.CopyTo(_writer.BaseStream);
                File.Delete(fileName);
                _writer.Write("\n");
            }
        }

        public void Dispose()
        {
            _writer.Dispose();
            _cleartool.Dispose();
        }
    }
}
