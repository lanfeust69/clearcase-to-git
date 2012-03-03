using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GitImporter
{
    /// <summary>
    /// List of versions that share :
    /// Author
    /// Branch
    /// Close C/I time
    /// TODO : check labels ?
    /// </summary>
    public class ChangeSet
    {
        public string Author { get; private set; }
        public string Branch { get; private set; }

        public DateTime StartTime { get; set; }
        public DateTime FinishTime { get; set; }

        public List<ElementVersion> Versions { get; private set; }
        public string Comment { get; set; }

        public ChangeSet(string author, string branch, DateTime time)
        {
            Author = author;
            Branch = branch;
            StartTime = time;
            FinishTime = time;

            Versions = new List<ElementVersion>();
        }
    }
}
