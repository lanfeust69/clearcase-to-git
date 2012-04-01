using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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
            var interestingFileChanges = Versions.Where(v => !v.NoComment && v.Name != null && !v.Version.Element.IsDirectory).ToList();
            int nbFileChanges = interestingFileChanges.Count;
            int nbDirectoryChanges = Versions.Where(v => !v.NoComment && v.Version.Element.IsDirectory).Count();
            if (nbFileChanges == 0)
                return nbDirectoryChanges > 0 ? nbDirectoryChanges + " director" + (nbDirectoryChanges > 1 ? "ies" : "y") + " modified" : "No actual change";
            
            var allComments = interestingFileChanges.Where(v => !string.IsNullOrWhiteSpace(v.Version.Comment))
                .Select(v => new { v.Name, v.Version.Comment })
                .GroupBy(e => (e.Comment ?? "").Trim().Replace("\r", ""))
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Select(v => v.Name).ToList());

            string title;
            if (nbDirectoryChanges > 0)
                title = string.Format("{0} file{1} and {2} director{3} modified",
                    nbFileChanges, (nbFileChanges > 1 ? "s" : ""), nbDirectoryChanges, nbDirectoryChanges > 1 ? "ies" : "y");
            else
                title = string.Format("{0} file{1} modified", nbFileChanges, (nbFileChanges > 1 ? "s" : ""));

            if (allComments.Count == 0)
                return title + " : " + DisplayFileNames(interestingFileChanges.Select(v => v.Name).ToList(), false);

            var mostFrequentComment = allComments.First();
            // no multi-line comment as title
            bool useMostFrequentCommentAsTitle = mostFrequentComment.Value.Count >= nbFileChanges / 2 + 1 && !mostFrequentComment.Key.Contains("\n");
            if (useMostFrequentCommentAsTitle)
                title = mostFrequentComment.Key + " (" + title + ")";

            if (useMostFrequentCommentAsTitle && allComments.Count == 1)
                return title + " : " + DisplayFileNames(interestingFileChanges.Select(v => v.Name).ToList(), false);

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
            return string.Format("{0}@{1} : {2} changes between {3} and {4}", AuthorName, Branch,
                Versions.Count + Renamed.Count + Removed.Count, StartTime, FinishTime);
        }
    }
}
