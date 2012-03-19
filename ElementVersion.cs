using System;
using System.Collections.Generic;

namespace GitImporter
{
    [Serializable]
    public class ElementVersion
    {
        public Element Element { get { return Branch.Element; } }
        public ElementBranch Branch { get; private set; }
        public int VersionNumber { get; private set; }
        public string Author { get; set; }
        public DateTime Date { get; set; }
        public string Comment { get; set; }

        public List<ElementVersion> MergesFrom { get; private set; }
        public List<ElementVersion> MergesTo { get; private set; }

        public List<string> Labels { get; private set; }

        public ElementVersion(ElementBranch branch, int versionNumber)
        {
            Branch = branch;
            VersionNumber = versionNumber;
            MergesFrom = new List<ElementVersion>();
            MergesTo = new List<ElementVersion>();
            Labels = new List<string>();
        }

        public override string ToString()
        {
            return Element.Name + "@@\\" + Branch.FullName + "\\" + VersionNumber;
        }
    }
}
