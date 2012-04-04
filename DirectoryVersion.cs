using System;
using System.Collections.Generic;
using System.Linq;
using ProtoBuf;

namespace GitImporter
{
    [Serializable]
    [ProtoContract]
    public class DirectoryVersion : ElementVersion
    {
        public List<KeyValuePair<string, Element>> Content { get; private set; }

        public DirectoryVersion(ElementBranch branch, int versionNumber) : base(branch, versionNumber)
        {
            Content = new List<KeyValuePair<string, Element>>();
        }

        // for Protobuf deserialization
        public DirectoryVersion()
        {}

        [ProtoMember(1)]
        private List<KeyValuePair<string, string>> _contentRaw;

        public void FixContent(Dictionary<string, Element> elementsByOid)
        {
            // ProtoBuf sends only items : no difference between an empty list and a null list
            Content = _contentRaw != null
                ? _contentRaw.Select(p => new KeyValuePair<string, Element>(p.Key, elementsByOid[p.Value])).ToList()
                : new List<KeyValuePair<string, Element>>();
            _contentRaw = null;
        }

        [ProtoBeforeDeserialization]
        private void BeforeProtobufDeserialization()
        {
            if (_contentRaw != null)
                Content = new List<KeyValuePair<string, Element>>();
        }

        [ProtoBeforeSerialization]
        private void BeforeProtobufSerialization()
        {
            _contentRaw = Content.Select(p => new KeyValuePair<string, string>(p.Key, p.Value.Oid)).ToList();
        }
    }
}
