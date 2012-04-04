using System;
using System.Collections.Generic;
using System.Linq;
using ProtoBuf;

namespace GitImporter
{
    [Serializable]
    [ProtoContract]
    public class ElementBranch
    {
        private string _fullName;

        public Element Element { get; private set; }
        [ProtoMember(1, AsReference = true)]
        public string BranchName { get; private set; }

        public ElementVersion BranchingPoint { get; private set; }
        [ProtoMember(2)] private ElementVersion.Reference _branchingPointReference;

        [ProtoMember(3)]
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

        // for Protobuf deserialization
        public ElementBranch()
        {}

        public override string ToString()
        {
            return Element.Name + "@@\\" + FullName;
        }

        [ProtoBeforeSerialization]
        private void BeforeProtobufSerialization()
        {
            if (BranchingPoint != null)
                _branchingPointReference = new ElementVersion.Reference(BranchingPoint);
        }

        public void Fixup(Element element)
        {
            Element = element;
            if (BranchingPoint == null && _branchingPointReference != null)
                BranchingPoint = Element.Branches[_branchingPointReference.BranchName].Versions
                    .First(v => v.VersionNumber == _branchingPointReference.VersionNumber);
            foreach (var version in Versions)
                version.Fixup(this);
        }
    }
}
