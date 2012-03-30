using System;
using System.Collections.Generic;
using System.Linq;

namespace GitImporter
{
    /// <summary>
    /// List of versions that share :
    /// Author
    /// Branch
    /// Close C/I time
    /// TODO : check labels ?
    /// </summary>
    public class ChangeSet
    {
        public class NamedVersion
        {
            public string Name { get; set; }
            public ElementVersion Version { get; set; }

            public override string ToString()
            {
                return Version + " as " + (Name ?? "<Unknown>");
            }
        }

        public class Comparer : IComparer<ChangeSet>
        {
            public int Compare(ChangeSet x, ChangeSet y)
            {
                return x.StartTime.CompareTo(y.StartTime);
            }
        }

        public string AuthorName { get; private set; }
        public string AuthorLogin { get; private set; }
        public string Branch { get; private set; }

        public DateTime StartTime { get; private set; }
        public DateTime FinishTime { get; private set; }

        public List<NamedVersion> Versions { get; private set; }
        public List<Tuple<string, string>> Renamed { get; private set; }
        public List<string> Removed { get; private set; }

        public int Id { get; set; }
        public ChangeSet BranchingPoint { get; set; }

        public ChangeSet(string authorName, string authorLogin, string branch, DateTime time)
        {
            AuthorName = authorName;
            AuthorLogin = authorLogin;
            Branch = branch;
            StartTime = time;
            FinishTime = time;

            Versions = new List<NamedVersion>();
            Renamed = new List<Tuple<string, string>>();
            Removed = new List<string>();
        }

        public void Add(ElementVersion version)
        {
            NamedVersion existing = Versions.Find(v => v.Version.Element == version.Element);
            if (existing != null)
            {
                // we are always on the same branch => we keep the latest version number,
                // which should always be the new version due to the way we retrieve them
                // TODO : this is not true for directory versions
                if (existing.Version.VersionNumber < version.VersionNumber)
                    existing.Version = version;
            }
            else
                Versions.Add(new NamedVersion { Version = version });
            if (version.Date < StartTime)
                StartTime = version.Date;
            if (version.Date > FinishTime)
                FinishTime = version.Date;
        }

        public string GetComment()
        {
            // TODO : better global comment
            return string.Join("\r\n",
                Versions.Select(v => new { v.Name, v.Version.Comment })
                    .GroupBy(e => e.Comment)
                    .Select(g => g.Count() > 3 ? g.Key : string.Join(", ", g.Select(p => p.Name)) + " : " + g.Key));
        }

        public override string ToString()
        {
            return string.Format("{0}@{1} : {2} changes between {3} and {4}", AuthorName, Branch,
                Versions.Count + Renamed.Count + Removed.Count, StartTime, FinishTime);
        }
    }
}
