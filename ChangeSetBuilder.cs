using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GitImporter
{
    public class ChangeSetBuilder
    {
        public static TraceSource Logger = Program.Logger;

        private readonly Dictionary<string, Element> _directoryElements;
        private readonly Dictionary<string, Element> _fileElements;
        private readonly Dictionary<string, Element> _elementsByOid;

        private List<Regex> _branchFilters;

        public ChangeSetBuilder(VobDB vobDB)
        {
            _directoryElements = vobDB.DirectoryElements;
            _fileElements = vobDB.FileElements;
            _elementsByOid = vobDB.ElementsByOid;
        }

        public void SetBranchFilters(string[] branches)
        {
            if (branches != null && branches.Length > 0)
                _branchFilters = branches.Select(b => new Regex(b)).ToList();
        }

        public void Build()
        {
        }
    }
}
