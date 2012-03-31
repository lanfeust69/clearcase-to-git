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
            public bool NoComment { get; set; }

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

        public NamedVersion Add(ElementVersion version)
        {
            return Add(version, null, false);
        }

        public NamedVersion Add(ElementVersion version, string name, bool noComment)
        {
            NamedVersion result;
            NamedVersion existing = Versions.Find(v => v.Version.Element == version.Element);
            if (!version.Element.IsDirectory && existing != null)
            {
                // we are always on the same branch => we keep the latest version number for file elements,
                // which should always be the new version due to the way we retrieve them
                if (existing.Name != null && name != null && existing.Name != name)
                    throw new Exception("Incompatible names for " + version + ", " + existing.Name + " != " + name);
                if (existing.Version.VersionNumber < version.VersionNumber)
                    existing.Version = version;
                result = existing;
            }
            else
                Versions.Add(result = new NamedVersion { Version = version, Name = name, NoComment = noComment });
            if (version.Date < StartTime)
                StartTime = version.Date;
            if (version.Date > FinishTime)
                FinishTime = version.Date;

            return result;
        }

        public string GetComment()
        {
            // TODO : better global comment
            return string.Join("\r\n",
                Versions.Where(v => !v.NoComment)
                    .Select(v => new { v.Name, v.Version.Comment })
                    .GroupBy(e => (e.Comment ?? "").Trim())
                    .Select(g => g.Count() > 3 ? g.Key : string.Join(", ", g.Select(p => p.Name)) + " : " + g.Key));
        }

        public override string ToString()
        {
            return string.Format("{0}@{1} : {2} changes between {3} and {4}", AuthorName, Branch,
                Versions.Count + Renamed.Count + Removed.Count, StartTime, FinishTime);
        }
    }
}
