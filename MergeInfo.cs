using System.Collections.Generic;

namespace GitImporter
{
    public class MergeInfo
    {
        public string From { get; private set; }
        public string To { get; private set; }

        public Dictionary<ChangeSet, ChangeSet> Merges { get; private set; }

        public Dictionary<ElementVersion, ChangeSet> MissingFromVersions { get; private set; }
        public Dictionary<ChangeSet, HashSet<ElementVersion>> MissingToVersionsByChangeSet { get; private set; }
        public Dictionary<ElementVersion, ChangeSet> MissingToVersions { get; private set; }

        public HashSet<ElementVersion> SeenFromVersions { get; private set; }
        public HashSet<ElementVersion> SeenToVersions { get; private set; }

        public MergeInfo(string from, string to)
        {
            From = from;
            To = to;
            Merges = new Dictionary<ChangeSet, ChangeSet>();
            MissingFromVersions = new Dictionary<ElementVersion, ChangeSet>();
            MissingToVersionsByChangeSet = new Dictionary<ChangeSet, HashSet<ElementVersion>>();
            MissingToVersions = new Dictionary<ElementVersion, ChangeSet>();
            SeenFromVersions = new HashSet<ElementVersion>();
            SeenToVersions = new HashSet<ElementVersion>();
        }

        public override string ToString()
        {
            return "Merges from " + From + " to " + To;
        }
    }
}
