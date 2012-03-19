using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GitImporter
{
    [Serializable]
    public class DirectoryVersion : ElementVersion
    {
        public List<KeyValuePair<string, Element>> Content { get; private set; }

        public DirectoryVersion(ElementBranch branch, int versionNumber) : base(branch, versionNumber)
        {
            Content = new List<KeyValuePair<string, Element>>();
        }
    }
}
