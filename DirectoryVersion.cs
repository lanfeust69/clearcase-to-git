using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GitImporter
{
    public class DirectoryVersion : ElementVersion
    {
        public List<string> Content { get; private set; }

        public DirectoryVersion()
        {
            Content = new List<string>();
        }
    }
}
