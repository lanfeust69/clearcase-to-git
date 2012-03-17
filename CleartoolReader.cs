using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GitImporter
{
    public class CleartoolReader : IDisposable
    {
        private readonly Cleartool _cleartool = new Cleartool();

        public Dictionary<string, Dictionary<string, ElementBranch>> FileElements { get; private set; }
        public Dictionary<string, Dictionary<string, ElementBranch>> DirectoryElements { get; private set; }

        public Dictionary<string, string> ElementsOids { get; private set; }

        public CleartoolReader()
        {
            DirectoryElements = new Dictionary<string, Dictionary<string, ElementBranch>>();
            ElementsOids = new Dictionary<string, string>();
        }

        public void Init(Dictionary<string, Dictionary<string, ElementBranch>> elements)
        {
            FileElements = elements;
            foreach (var element in FileElements.Keys)
            {
                string oid = _cleartool.Oid(element);
                ElementsOids[oid] = element;
            }
        }

        internal void ReadDirectory(string dir)
        {
        }

        internal void ReadElement(string element)
        {
        }

        public void Dispose()
        {
            _cleartool.Dispose();
        }
    }
}
