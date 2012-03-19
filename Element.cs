using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GitImporter
{
    [Serializable]
    public class Element
    {
        public string Name { get; private set; }
        public bool IsDirectory { get; private set; }
        public string Oid { get; set; }

        public Dictionary<string, ElementBranch> Branches { get; private set; }

        // we don't have the oid when reading from an export file, it is filled in later
        public Element(string name, bool isDirectory)
        {
            Name = name;
            IsDirectory = isDirectory;
            Branches = new Dictionary<string, ElementBranch>();
        }

        public override string ToString()
        {
            return Name;
        }
    }
}
