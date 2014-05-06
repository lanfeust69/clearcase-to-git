using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GitImporter
{
    public class LabelFilter
    {
        private readonly List<Regex> _labelsRegexToKeep;
        private readonly HashSet<string> _labelsToKeep = new HashSet<string>();
        private readonly HashSet<string> _labelsToTrash = new HashSet<string>();

        private readonly object _syncroot = new object();

        public LabelFilter(IEnumerable<string> labels)
        {
            _labelsRegexToKeep = new List<Regex>(labels
                .Where(l => !string.IsNullOrWhiteSpace(l) && l.ToUpper() != "NONE")
                .Select(l => new Regex(l, RegexOptions.Compiled)));
        }

        public bool ShouldKeep(string label)
        {
            lock (_syncroot)
            {
                if (_labelsToTrash.Contains(label))
                    return false;
                if (!_labelsToKeep.Contains(label))
                {
                    // first time seen
                    if (_labelsRegexToKeep.TrueForAll(r => !r.IsMatch(label)))
                    {
                        _labelsToTrash.Add(label);
                        return false;
                    }
                    _labelsToKeep.Add(label);
                }
                return true;
            }
        }
    }
}
