using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GitImporter
{
    [Serializable]
    public class VobDB
    {
        public Dictionary<string, Element> FileElements { get; private set; }
        public Dictionary<string, Element> DirectoryElements { get; private set; }
        public Dictionary<string, Element> ElementsByOid { get; private set; }

        public VobDB(Dictionary<string, Element> directoryElements, Dictionary<string, Element> fileElements, Dictionary<string, Element> elementsByOid)
        {
            DirectoryElements = directoryElements;
            FileElements = fileElements;
            ElementsByOid = elementsByOid;
        }
    }
}
