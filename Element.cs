using System;
using System.Collections.Generic;
using System.Linq;
using ProtoBuf;

namespace GitImporter
{
    [Serializable]
    [ProtoContract]
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
            Branches = _rawElementsBranches.ToDictionary(b => b.BranchName);
            foreach (var branch in _rawElementsBranches)
                branch.Fixup(this);

            _rawElementsBranches = null;
        }
    }
}
