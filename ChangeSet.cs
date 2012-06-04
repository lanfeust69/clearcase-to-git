using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;

namespace GitImporter
{
    /// <summary>
    /// List of versions that share :
    /// Author
    /// Branch
    /// Close C/I time
    /// </summary>
    public class ChangeSet
    {
        public class NamedVersion
        {
            public List<string> Names { get; private set; }
            public ElementVersion Version { get; set; }
            public bool InRawChangeSet { get; set; }

            public NamedVersion()
            {
                Names = new List<string>();
            }

            public NamedVersion(ElementVersion version, string name, bool inRawChangeSet) : this()
            {
                Version = version;
                InRawChangeSet = inRawChangeSet;
                if (name != null)
                    Names.Add(name);
            }

            public override string ToString()
            {
                return Version + " as " + (Names.Count == 0 ? "<Unknown>" : string.Join(", ", Names));
            }
        }

        public class Comparer : IComparer<ChangeSet>
        {
            public int Compare(ChangeSet x, ChangeSet y)
            {
                return x.StartTime.CompareTo(y.StartTime);
            }
        }

        public static TraceSource Logger = Program.Logger;

        public int Id { get; set; }

        public string AuthorName { get; private set; }
        public string AuthorLogin { get; private set; }
        public string Branch { get; private set; }

        public DateTime StartTime { get; private set; }
        public DateTime FinishTime { get; private set; }

        public List<NamedVersion> Versions { get; private set; }
        /// <summary>
        /// SkippedVersions are usefull only for mergesTo : if there is a merge to a skipped version,
        /// the actual (more recent) version of the corresponding element is also merged
        /// </summary>
        public List<ElementVersion> SkippedVersions { get; private set; }
        public List<Tuple<string, string>> Renamed { get; private set; }
        public List<string> Removed { get; private set; }
        public List<Tuple<string, string>> Copied { get; private set; }
        public List<Tuple<string, string>> SymLinks { get; private set; }

        public ChangeSet BranchingPoint { get; set; }
        public bool IsBranchingPoint { get; set; }

        public List<ChangeSet> Merges { get; private set; }
        public bool IsMerged { get; set; }

        public List<string> Labels { get; set; }

        public bool IsEmpty
        {
            get
            {
                return Versions.Where(v => !v.Version.Element.IsDirectory && v.Names.Count > 0).Count() == 0 &&
                    Renamed.Count == 0 && Removed.Count == 0 && Copied.Count == 0 &&
                    SymLinks.Count == 0 && Labels.Count == 0 && !IsBranchingPoint &&
                    Merges.Count == 0 && !IsMerged;
            }
        }

        public ChangeSet(string authorName, string authorLogin, string branch, DateTime time)
        {
            AuthorName = authorName;
            AuthorLogin = authorLogin;
            Branch = branch;
            StartTime = time;
            FinishTime = time;

            Versions = new List<NamedVersion>();
            SkippedVersions = new List<ElementVersion>();
            Renamed = new List<Tuple<string, string>>();
            Removed = new List<string>();
            Copied = new List<Tuple<string, string>>();
            SymLinks = new List<Tuple<string, string>>();

            Merges = new List<ChangeSet>();

            Labels = new List<string>();
        }

        public NamedVersion Add(ElementVersion version)
        {
            return Add(version, null, true);
        }

        public NamedVersion Add(ElementVersion version, string name, bool inRawChangeSet)
        {
            NamedVersion result;
            NamedVersion existing = Versions.Find(v => v.Version.Element == version.Element);
            if (!version.Element.IsDirectory && existing != null)
            {
                // we are always on the same branch => we keep the latest version number for file elements,
                // which should always be the new version due to the way we retrieve them
                if (existing.Names.Count > 0 && name != null && !existing.Names.Contains(name))
                {
                    existing.Names.Add(name);
                    Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet,
                        "Version " + version + " has several names : " + string.Join(", ", existing.Names));
                }
                ElementVersion skippedVersion = null;
                if (existing.Version.VersionNumber < version.VersionNumber)
                {
                    skippedVersion = existing.Version;
                    existing.Version = version;
                }
                else if (existing.Version.VersionNumber > version.VersionNumber)
                    skippedVersion = version;
                if (skippedVersion != null && (skippedVersion.Labels.Count > 0 || skippedVersion.MergesFrom.Count > 0 || skippedVersion.MergesTo.Count > 0))
                    SkippedVersions.Add(skippedVersion);
                result = existing;
            }
            else
                Versions.Add(result = new NamedVersion(version, name, inRawChangeSet));
            if (inRawChangeSet)
            {
                if (version.Date < StartTime)
                    StartTime = version.Date;
                if (version.Date > FinishTime)
                    FinishTime = version.Date;
            }

            return result;
        }

        public string GetComment()
        {
            var interestingFileChanges = Versions.Where(v => v.InRawChangeSet && v.Names.Count > 0 && !v.Version.Element.IsDirectory).ToList();
            int nbFileChanges = interestingFileChanges.Count;
            int nbTreeChanges = Removed.Count + Renamed.Count + Copied.Count + SymLinks.Count +
                Versions.Where(v => !v.InRawChangeSet && v.Names.Count > 0 && !v.Version.Element.IsDirectory).Count();
            if (nbFileChanges == 0)
                return nbTreeChanges > 0 ? nbTreeChanges + " tree modification" + (nbTreeChanges > 1 ? "s" : "") : "No actual change";

            var allComments = interestingFileChanges.Where(v => !string.IsNullOrWhiteSpace(v.Version.Comment))
                .Select(v => new { Name = v.Names[0], v.Version.Comment })
                .GroupBy(e => (e.Comment ?? "").Trim().Replace("\r", ""))
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Select(v => v.Name).ToList());

            string title;
            if (nbTreeChanges > 0)
                title = string.Format("{0} file modification{1} and {2} tree modification{3}",
                    nbFileChanges, (nbFileChanges > 1 ? "s" : ""), nbTreeChanges, nbTreeChanges > 1 ? "s" : "");
            else
                title = string.Format("{0} file modification{1}", nbFileChanges, (nbFileChanges > 1 ? "s" : ""));

            if (allComments.Count == 0)
                return title + " : " + DisplayFileNames(interestingFileChanges.Select(v => v.Names[0]).ToList(), false);

            var mostFrequentComment = allComments.First();
            // no multi-line comment as title
            bool useMostFrequentCommentAsTitle = mostFrequentComment.Value.Count >= nbFileChanges / 2 + 1 && !mostFrequentComment.Key.Contains("\n");
            if (useMostFrequentCommentAsTitle)
                title = mostFrequentComment.Key + " (" + title + ")";

            if (useMostFrequentCommentAsTitle && allComments.Count == 1)
                return title + " : " + DisplayFileNames(interestingFileChanges.Select(v => v.Names[0]).ToList(), false);

            var sb = new StringBuilder(title);
            sb.Append("\n");
            foreach (var comment in allComments)
            {
                sb.Append("\n");
                sb.Append(DisplayFileNames(comment.Value, true));
                sb.Append(" :\n\t");
                sb.Append(comment.Key.Replace("\n", "\n\t"));
            }

            return sb.ToString();
        }

        private static string DisplayFileNames(IList<string> fileNames, bool showNbNonDisplayed)
        {
            const int defaultNbToDisplay = 3;
            int nbToDisplay = fileNames.Count > defaultNbToDisplay + 1 ? defaultNbToDisplay : fileNames.Count;
            var sb = new StringBuilder();
            for (int i = 0; i < nbToDisplay; i++)
            {
                if (i != 0)
                    sb.Append(", ");
                int pos = fileNames[i].LastIndexOf('/');
                sb.Append(pos == -1 ? fileNames[i] : fileNames[i].Substring(pos + 1));
            }
            if (fileNames.Count > defaultNbToDisplay + 1)
            {
                sb.Append(", ...");
                if (showNbNonDisplayed)
                    sb.Append(" (" + (fileNames.Count - defaultNbToDisplay) + " more)");
            }
            return sb.ToString();
        }

        public override string ToString()
        {
            return string.Format("Id {0}, {1}@{2} : {3} changes between {4} and {5}", Id, AuthorName, Branch,
                Versions.Count + Renamed.Count + Removed.Count + SymLinks.Count, StartTime, FinishTime);
        }
    }
}
