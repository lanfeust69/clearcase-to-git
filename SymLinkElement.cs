using System;
using System.Collections.Generic;
using ProtoBuf;

namespace GitImporter
{
    [Serializable]
    [ProtoContract]
    public class SymLinkElement : Element
    {
        public const string SYMLINK = "symlink:";

        public DirectoryVersion Directory { get; private set; }
        [ProtoMember(1)] private ElementVersion.Reference _directory;

        public SymLinkElement(DirectoryVersion directory, string name)
            : base(directory + "\\" + name.Substring(SYMLINK.Length), false)
        {
            Oid = SYMLINK + Name;
            Directory = directory;
        }

        // for Protobuf deserialization
        public SymLinkElement()
        {}

        [ProtoBeforeSerialization]
        private void BeforeProtobufSerialization()
        {
            _directory = new ElementVersion.Reference(Directory);
        }

        public void Fixup(Dictionary<string, Element> elementsByOid)
        {
            Directory = (DirectoryVersion)elementsByOid[_directory.ElementOid]
                .GetVersion(_directory.BranchName, _directory.VersionNumber);
        }
    }
}
