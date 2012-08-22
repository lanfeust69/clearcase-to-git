using System;
using System.Collections.Generic;
using System.Linq;
using ProtoBuf;

namespace GitImporter
{
    [Serializable]
    [ProtoContract, ProtoInclude(100, typeof(DirectoryVersion))]
    public class ElementVersion
    {
        /// <summary>
        /// Used for serialization, to avoid using references
        /// </summary>
        [ProtoContract]
        public class Reference
        {
            [ProtoMember(1, AsReference = true)] public string ElementOid;
            [ProtoMember(2, AsReference = true)] public string BranchName;
            [ProtoMember(3)] public int VersionNumber;

            public Reference()
            {}

            public Reference(ElementVersion version)
            {
                ElementOid = version.Element.Oid;
                BranchName = version.Branch.BranchName;
                VersionNumber = version.VersionNumber;
            }
        }

        public Element Element { get { return Branch.Element; } }
        public ElementBranch Branch { get; private set; }
        [ProtoMember(1)]
        public int VersionNumber { get; private set; }
        [ProtoMember(2, AsReference = true)]
        public string AuthorName { get; set; }
        [ProtoMember(3, AsReference = true)]
        public string AuthorLogin { get; set; }
        [ProtoMember(4)]
        public DateTime Date { get; set; }
        [ProtoMember(5, AsReference = true)]
        public string Comment { get; set; }

        public List<ElementVersion> MergesFrom { get; private set; }
        public List<ElementVersion> MergesTo { get; private set; }

        [ProtoMember(6, AsReference = true)]
        public List<string> Labels { get; private set; }

        public string VersionPath { get { return "\\" + Branch.FullName + "\\" + VersionNumber; } } 

        public ElementVersion(ElementBranch branch, int versionNumber)
        {
            Branch = branch;
            VersionNumber = versionNumber;
            MergesFrom = new List<ElementVersion>();
            MergesTo = new List<ElementVersion>();
            Labels = new List<string>();
        }

        // for Protobuf deserialization
        public ElementVersion()
        {}

        public ElementVersion GetPreviousVersion()
        {
            if (VersionNumber == 0)
                return Branch.BranchingPoint; // null for "main"
            return Branch.Versions[Branch.Versions.IndexOf(this) - 1];
        }

        public bool IsAncestorOf(ElementVersion version)
        {
            if (Element != version.Element)
                return false;
            if (Branch == version.Branch)
                return VersionNumber < version.VersionNumber;
            if (version.Branch.BranchingPoint != null)
                return IsAncestorOf(version.Branch.BranchingPoint);
            return false;
        }

        public override string ToString()
        {
            return Element.Name + "@@" + VersionPath;
        }

        [ProtoMember(7)] private List<Reference> _rawMergesFrom;
        [ProtoMember(8)] private List<Reference> _rawMergesTo;

        [ProtoBeforeSerialization]
        private void BeforeProtobufSerialization()
        {
            if (MergesFrom.Count > 0)
                _rawMergesFrom = MergesFrom.Select(v => new Reference(v)).ToList();
            if (MergesTo.Count > 0)
                _rawMergesTo = MergesTo.Select(v => new Reference(v)).ToList();
        }

        public void Fixup(ElementBranch branch)
        {
            Branch = branch;
            MergesFrom = _rawMergesFrom == null ? new List<ElementVersion>()
                : _rawMergesFrom.Select(r => Element.GetVersion(r.BranchName, r.VersionNumber)).ToList();
            _rawMergesFrom = null;
            MergesTo = _rawMergesTo == null ? new List<ElementVersion>()
                : _rawMergesTo.Select(r => Element.GetVersion(r.BranchName, r.VersionNumber)).ToList();
            _rawMergesTo = null;
            if (Labels == null)
                Labels = new List<string>();
        }
    }
}
