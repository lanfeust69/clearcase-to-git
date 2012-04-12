using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ProtoBuf;

namespace GitImporter
{
    [Serializable]
    [ProtoContract, ProtoInclude(100, typeof(Element))]
    public class SymLinkElement : Element
    {
        public const string SYMLINK = "symlink:";

        public DirectoryVersion Directory { get; private set; }
        [ProtoMember(1)] private ElementVersion.Reference _directory;

        public SymLinkElement(DirectoryVersion version, string name)
            : base(version.ToString() + "\\" + name.Substring(SYMLINK.Length), false)
        {
            Oid = SYMLINK + Name;
        }

        [ProtoBeforeSerialization]
        private void BeforeProtobufSerialization()
        {
            _directory = new ElementVersion.Reference(Directory);
        }

        public void Fixup(Dictionary<string, Element> elementsByOid)
        {
            Directory = (DirectoryVersion)elementsByOid[_directory.ElementOid]
                .Branches[_directory.BranchName]
                .Versions.First(v => v.VersionNumber == _directory.VersionNumber);
        }
    }
}
