using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ProtoBuf;

namespace GitImporter
{
    [Serializable]
    [ProtoContract]
    public class VobDB
    {
        public Dictionary<string, Element> ElementsByOid { get; private set; }

        public VobDB(Dictionary<string, Element> elementsByOid)
        {
            ElementsByOid = elementsByOid;
        }

        public VobDB()
        {
            ElementsByOid = new Dictionary<string, Element>();
        }

        public void Add(VobDB other)
        {
            foreach (var pair in other.ElementsByOid)
            {
                Element existing;
                if (!ElementsByOid.TryGetValue(pair.Key, out existing))
                {
                    ElementsByOid.Add(pair.Key, pair.Value);
                    continue;
                }
                // TODO : we should keep the one with the most versions/branches
                if (existing.Name != pair.Value.Name)
                    Program.Logger.TraceData(TraceEventType.Information, 0,
                        string.Format("element with oid {0} has a different name : keeping {1}, ignoring {2}", existing.Oid, existing.Name, pair.Value.Name));
            }
        }

        [ProtoMember(1)]
        private List<Element> _rawElements;

        [ProtoBeforeSerialization]
        private void BeforeProtobufSerialization()
        {
            _rawElements = new List<Element>(ElementsByOid.Values);
        }

        [ProtoAfterDeserialization]
        private void AfterProtobufDeserialization()
        {
            if (_rawElements == null)
            {
                ElementsByOid = new Dictionary<string, Element>();
                return;
            }
            ElementsByOid = _rawElements.ToDictionary(e => e.Oid);
            foreach (var element in _rawElements)
            {
                var symlink = element as SymLinkElement;
                if (symlink != null)
                    symlink.Fixup(ElementsByOid);

                foreach (var branch in element.Branches.Values)
                    foreach (var version in branch.Versions.OfType<DirectoryVersion>())
                        version.FixContent(ElementsByOid);
            }
            _rawElements = null;
        }
    }
}
