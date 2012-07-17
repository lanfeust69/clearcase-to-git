using System;
using System.Collections.Generic;
using System.Linq;
using ProtoBuf;

namespace GitImporter
{
    [Serializable]
    [ProtoContract, ProtoInclude(100, typeof(SymLinkElement))]
    public class Element
    {
        [ProtoMember(1)]
        public string Name { get; set; }
        [ProtoMember(2)]
        public bool IsDirectory { get; private set; }
        [ProtoMember(3)]
        public string Oid { get; set; }

        public Dictionary<string, ElementBranch> Branches { get; private set; }

        // we don't have the oid when reading from an export file, it is filled in later
        public Element(string name, bool isDirectory)
        {
            Name = name;
            IsDirectory = isDirectory;
            Branches = new Dictionary<string, ElementBranch>();
        }

        // for Protobuf deserialization
        public Element()
        {}

        public ElementVersion GetVersion(string branchName, int versionNumber)
        {
            ElementBranch branch;
            if (!Branches.TryGetValue(branchName, out branch))
                return null;
            // could be faster with a List.BinarySearch
            return branch.Versions.FirstOrDefault(v => v.VersionNumber == versionNumber);
        }

        public override string ToString()
        {
            return Name;
        }

        [ProtoMember(4)]
        private List<ElementBranch> _rawElementsBranches;

        [ProtoBeforeSerialization]
        private void BeforeProtobufSerialization()
        {
            _rawElementsBranches = new List<ElementBranch>(Branches.Values);
        }

        [ProtoAfterDeserialization]
        private void AfterProtobufDeserialization()
        {
            // no branches in SymLinkElement
            if (_rawElementsBranches != null)
            {
                Branches = _rawElementsBranches.ToDictionary(b => b.BranchName);
                foreach (var branch in _rawElementsBranches)
                {
                    // empty branch possible if all versions were too recent
                    // in this case, protobuf leaves a null Versions property
                    if (branch.Versions == null)
                        Branches.Remove(branch.BranchName);
                    else
                        branch.Fixup(this);
                }
            }
            else
                Branches = new Dictionary<string, ElementBranch>();

            _rawElementsBranches = null;
        }
    }
}
