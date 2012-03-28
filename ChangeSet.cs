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
        public class Comparer : IComparer<ChangeSet>
        {
            public int Compare(ChangeSet x, ChangeSet y)
            {
                return x.StartTime.CompareTo(y.StartTime);
            }
        }

        public string Author { get; private set; }
        public string Branch { get; private set; }

        public DateTime StartTime { get; set; }
        public DateTime FinishTime { get; set; }

        public List<ElementVersion> Versions { get; private set; }

        public ChangeSet(string author, string branch, DateTime time)
        {
            Author = author;
            Branch = branch;
            StartTime = time;
            FinishTime = time;

            Versions = new List<ElementVersion>();
        }

        public void Add(ElementVersion version)
        {
            ElementVersion existing = Versions.Find(v => v.Element == version.Element);
            if (existing != null)
            {
                // we are always on the same branch => we keep the latest version number,
                // which should always be the new version due to the way we retrieve them
                if (existing.VersionNumber < version.VersionNumber)
                {
                    Versions.Remove(existing);
                    Versions.Add(version);
                }
            }
            else
                Versions.Add(version);
            if (version.Date < StartTime)
                StartTime = version.Date;
            if (version.Date > FinishTime)
                FinishTime = version.Date;
        }

        public string GetComment()
        {
            return string.Join("\r\n",
                Versions.Select(v => new { v.Element.Name, v.Comment })
                    .GroupBy(e => e.Comment)
                    .Select(g => g.Count() > 3 ? g.Key : string.Join(", ", g.Select(p => p.Name)) + " : " + g.Key));
        }

        public override string ToString()
        {
            return string.Format("{0}@{1} : {2} changes between {3} and {4}", Author, Branch, Versions.Count, StartTime, FinishTime);
        }
    }
}
