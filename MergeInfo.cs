using System.Collections.Generic;

namespace GitImporter
{
    public class MergeInfo
    {
        public string From { get; private set; }
        public string To { get; private set; }

        public ChangeSet CurrentFrom { get; set; }
        public ChangeSet CurrentTo { get; set; }

        public HashSet<ElementVersion> MissingFromVersions { get; private set; }
        public HashSet<ElementVersion> MissingToVersions { get; private set; }

        public HashSet<ElementVersion> SeenFromVersions { get; private set; }
        public HashSet<ElementVersion> SeenToVersions { get; private set; }

        public MergeInfo(string from, string to)
        {
            From = from;
            To = to;
            MissingFromVersions = new HashSet<ElementVersion>();
            MissingToVersions = new HashSet<ElementVersion>();
            SeenFromVersions = new HashSet<ElementVersion>();
            SeenToVersions = new HashSet<ElementVersion>();
        }

        public override string ToString()
        {
            return "Merge from " + From + " to " + To;
        }
    }
}
