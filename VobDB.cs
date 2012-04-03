using System;
using System.Collections.Generic;

namespace GitImporter
{
    [Serializable]
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
                if (existing.Name != pair.Value.Name)
                    throw new Exception(string.Format("Name mismatchElement with oid {0} : {1} != {2}", existing.Oid, existing.Name, pair.Value.Name));
            }
        }
    }
}
