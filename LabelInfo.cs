using System.Collections.Generic;
using System.Linq;

namespace GitImporter
{
    public class LabelInfo
    {
        public string Name { get; private set; }
        public List<ElementVersion> Versions { get; private set; }
        public HashSet<ElementVersion> MissingVersions { get; private set; }

        public LabelInfo(string name)
        {
            Name = name;
            Versions = new List<ElementVersion>();
        }

        public void Reset()
        {
            MissingVersions = new HashSet<ElementVersion>(Versions.Where(v => v.VersionNumber != 0));
        }
    }
}
