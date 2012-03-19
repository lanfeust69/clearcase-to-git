using System;
using System.Collections.Generic;

namespace GitImporter
{
    [Serializable]
    public class ElementBranch
    {
        private string _fullName;

        public Element Element { get; private set; }
        public string BranchName { get; private set; }
        public ElementVersion BranchingPoint { get; private set; }

        public List<ElementVersion> Versions { get; private set; }

        public string FullName
        {
            get { return _fullName ?? (_fullName = (BranchingPoint == null ? "" : BranchingPoint.Branch.FullName + "\\") + BranchName); }
        }

        public ElementBranch(Element element, string branchName, ElementVersion branchingPoint)
        {
            Element = element;
            BranchName = branchName;
            BranchingPoint = branchingPoint;
            Versions = new List<ElementVersion>();
        }

        public override string ToString()
        {
            return Element.Name + "@@\\" + FullName;
        }
    }
}
