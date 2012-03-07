using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GitImporter
{
    public class ElementBranch
    {
        private string _fullName;

        public string ElementName { get; set; }
        public string BranchName { get; set; }
        public ElementVersion BranchingPoint { get; set; }

        public List<ElementVersion> Versions { get; private set; }

        public string FullName
        {
            get { return _fullName ?? (_fullName = (BranchingPoint == null ? "" : BranchingPoint.Branch.FullName + "\\") + BranchName); }
        }

        public ElementBranch()
        {
            Versions = new List<ElementVersion>();
        }

        public override string ToString()
        {
            return ElementName + "@@\\" + FullName;
        }
    }
}
