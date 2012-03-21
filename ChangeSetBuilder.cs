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

        private const int MAX_DELAY = 15;
        private static readonly ChangeSet.Comparer _comparer = new ChangeSet.Comparer();

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

        public IList<ChangeSet> Build()
        {
            var changeSets = CreateChangeSets();
            return changeSets.Values.SelectMany(d => d.Values.SelectMany(l => l)).OrderBy(c => c, _comparer).ToList();
        }

        private Dictionary<string, Dictionary<string, List<ChangeSet>>> CreateChangeSets()
        {
            // the list must always be kept sorted, so that BinarySearch works
            // if the size of the list gets too big and (mostly) linear insertion time becomes a problem,
            // we could look at SorteList<> (which is not actually a list, but a dictionary)
            var changeSets = new Dictionary<string, Dictionary<string, List<ChangeSet>>>();
            foreach (var element in _elementsByOid.Values)
                foreach (var branch in element.Branches.Values)
                {
                    if (branch.BranchName != "main" && _branchFilters != null && !_branchFilters.Any(e => e.IsMatch(branch.BranchName)))
                        continue;
                    Dictionary<string, List<ChangeSet>> branchChangeSets;
                    if (!changeSets.TryGetValue(branch.BranchName, out branchChangeSets))
                    {
                        branchChangeSets = new Dictionary<string, List<ChangeSet>>();
                        changeSets.Add(branch.BranchName, branchChangeSets);
                    }
                    foreach (var version in branch.Versions)
                    {
                        if (version.VersionNumber == 0)
                            continue;
                        List<ChangeSet> authorChangeSets;
                        if (!branchChangeSets.TryGetValue(version.Author, out authorChangeSets))
                        {
                            authorChangeSets = new List<ChangeSet>();
                            branchChangeSets.Add(version.Author, authorChangeSets);
                        }
                        AddVersion(authorChangeSets, version);
                    }
                }
            return changeSets;
        }

        private static void AddVersion(List<ChangeSet> changeSets, ElementVersion version)
        {
            // used either for search or for new ChangeSet
            var changeSet = new ChangeSet(version.Author, version.Branch.BranchName, version.Date);
            if (changeSets.Count == 0)
            {
                changeSet.Add(version);
                changeSets.Add(changeSet);
                return;
            }

            int index = changeSets.BinarySearch(changeSet, _comparer);
            if (index >= 0)
            {
                changeSets[index].Add(version);
                return;
            }

            index = ~index; // index of first element bigger
            if (index == changeSets.Count)
            {
                // so even the last one is not bigger
                ChangeSet candidate = changeSets[index - 1];
                if (version.Date <= candidate.FinishTime.AddSeconds(MAX_DELAY))
                    candidate.Add(version);
                else
                {
                    changeSet.Add(version);
                    changeSets.Add(changeSet);
                }
                return;
            }
            if (index == 0)
            {
                ChangeSet candidate = changeSets[0];
                if (version.Date >= candidate.StartTime.AddSeconds(-MAX_DELAY))
                    candidate.Add(version);
                else
                {
                    changeSet.Add(version);
                    changeSets.Insert(0, changeSet);
                }
                return;
            }
            DateTime lowerBound = changeSets[index - 1].FinishTime;
            DateTime upperBound = changeSets[index].StartTime;
            if (version.Date <= lowerBound.AddSeconds(MAX_DELAY) && version.Date < upperBound.AddSeconds(-MAX_DELAY))
            {
                changeSets[index - 1].Add(version);
                return;
            }
            if (version.Date > lowerBound.AddSeconds(MAX_DELAY) && version.Date >= upperBound.AddSeconds(-MAX_DELAY))
            {
                changeSets[index].Add(version);
                return;
            }
            if (version.Date > lowerBound.AddSeconds(MAX_DELAY) && version.Date < upperBound.AddSeconds(-MAX_DELAY))
            {
                changeSet.Add(version);
                changeSets.Insert(index, changeSet);
                return;
            }
            // last case : we should merge the two ChangeSets (that are now "linked" by the version we are adding)
            changeSets[index - 1].Add(version);
            foreach (var v in changeSets[index].Versions)
                changeSets[index - 1].Add(v);
            changeSets.RemoveAt(index);
        }
    }
}
