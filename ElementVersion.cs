using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GitImporter
{
    class ElementVersion
    {
        public string ElementName { get; set; }
        public ElementBranch Branch { get; set; }
        public int VersionNumber { get; set; }
        public string Author { get; set; }
        public DateTime Date { get; set; }
        public string Comment { get; set; }

        public List<ElementVersion> MergesFrom { get; private set; }
        public List<ElementVersion> MergesTo { get; private set; }

        public List<string> Labels { get; private set; }

        public ElementVersion()
        {
            MergesFrom = new List<ElementVersion>();
            MergesTo = new List<ElementVersion>();
            Labels = new List<string>();
        }

        public override string ToString()
        {
            return ElementName + "@@\\" + Branch.FullName + "\\" + VersionNumber;
        }
    }
}
