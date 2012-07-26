using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace GitImporter
{
    public class RawHistoryBuilder
    {
        public static TraceSource Logger = Program.Logger;

        private const int MAX_DELAY = 20;
        private static readonly ChangeSet.Comparer _comparer = new ChangeSet.Comparer();

        private readonly Dictionary<string, Element> _elementsByOid;
        private List<Regex> _branchFilters;

        // ChangeSets, grouped first by branch, then by author
        private Dictionary<string, Dictionary<string, List<ChangeSet>>> _changeSets;

        /// <summary>
        /// For each branch, its parent branch
        /// </summary>
        public Dictionary<string, string> GlobalBranches { get; private set; }

        public Dictionary<string, LabelInfo> Labels { get; private set; }

        public RawHistoryBuilder(VobDB vobDB)
        {
            if (vobDB != null)
                _elementsByOid = vobDB.ElementsByOid;
            Labels = new Dictionary<string, LabelInfo>();
        }

        public void SetBranchFilters(string[] branches)
        {
            if (branches != null && branches.Length > 0)
                _branchFilters = branches.Select(b => new Regex(b)).ToList();
        }

        public List<ChangeSet> Build(List<ElementVersion> newVersions)
        {
            var allElementBranches = CreateRawChangeSets(newVersions);
            ComputeGlobalBranches(allElementBranches);
            FilterBranches();
            FilterLabels();
            return _changeSets.Values.SelectMany(d => d.Values.SelectMany(l => l)).OrderBy(c => c, _comparer).ToList();
        }

        private IEnumerable<string> CreateRawChangeSets(IEnumerable<ElementVersion> newVersions)
        {
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Start creating raw ChangeSets");
            // the list must always be kept sorted, so that BinarySearch works
            // if the size of the list gets too big and (mostly) linear insertion time becomes a problem,
            // we could look at SorteList<> (which is not actually a list, but a dictionary)
            _changeSets = new Dictionary<string, Dictionary<string, List<ChangeSet>>>();
            // keep all FullName's, so that we can try to guess "global" BranchingPoint
            var allElementBranches = new HashSet<string>();
            if (newVersions != null)
            {
                var allNewVersions = new HashSet<ElementVersion>(newVersions);
                foreach (var version in newVersions)
                {
                    allElementBranches.Add(version.Branch.FullName);
                    Dictionary<string, List<ChangeSet>> branchChangeSets;
                    if (!_changeSets.TryGetValue(version.Branch.BranchName, out branchChangeSets))
                    {
                        branchChangeSets = new Dictionary<string, List<ChangeSet>>();
                        _changeSets.Add(version.Branch.BranchName, branchChangeSets);
                    }
                    ProcessVersion(version, branchChangeSets, allNewVersions);
                }
            }
            else
            {
                foreach (var element in _elementsByOid.Values)
                    foreach (var branch in element.Branches.Values)
                    {
                        allElementBranches.Add(branch.FullName);
                        Dictionary<string, List<ChangeSet>> branchChangeSets;
                        if (!_changeSets.TryGetValue(branch.BranchName, out branchChangeSets))
                        {
                            branchChangeSets = new Dictionary<string, List<ChangeSet>>();
                            _changeSets.Add(branch.BranchName, branchChangeSets);
                        }
                        foreach (var version in branch.Versions)
                            ProcessVersion(version, branchChangeSets, null);
                    }
            }

            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Stop creating raw ChangeSets");
            return allElementBranches;
        }

        private void ProcessVersion(ElementVersion version, Dictionary<string, List<ChangeSet>> branchChangeSets, HashSet<ElementVersion> newVersions)
        {
            // we don't really handle versions 0 on branches : always consider BranchingPoint
            ElementVersion versionForLabel = version;
            while (versionForLabel.VersionNumber == 0 && versionForLabel.Branch.BranchingPoint != null)
                versionForLabel = versionForLabel.Branch.BranchingPoint;
            // don't "move" the label on versions that won't be processed (we need to assume these are correct)
            if (newVersions == null || newVersions.Contains(versionForLabel))
            {
                foreach (var label in version.Labels)
                {
                    LabelInfo labelInfo;
                    if (!Labels.TryGetValue(label, out labelInfo))
                    {
                        labelInfo = new LabelInfo(label);
                        Labels.Add(label, labelInfo);
                    }
                    labelInfo.Versions.Add(versionForLabel);
                    // also actually "move" the label
                    if (versionForLabel != version)
                        versionForLabel.Labels.Add(label);
                }
            }
            // end of label move
            if (versionForLabel != version)
                version.Labels.Clear();
            if (version.VersionNumber == 0 && (version.Element.IsDirectory || version.Branch.BranchName != "main"))
                return;
            List<ChangeSet> authorChangeSets;
            if (!branchChangeSets.TryGetValue(version.AuthorLogin, out authorChangeSets))
            {
                authorChangeSets = new List<ChangeSet>();
                branchChangeSets.Add(version.AuthorLogin, authorChangeSets);
            }
            AddVersion(authorChangeSets, version);
        }

        private static void AddVersion(List<ChangeSet> changeSets, ElementVersion version)
        {
            // used either for search or for new ChangeSet
            var changeSet = new ChangeSet(version.AuthorName, version.AuthorLogin, version.Branch.BranchName, version.Date);
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
                changeSets[index - 1].Add(v.Version);
            changeSets.RemoveAt(index);
        }

        private void ComputeGlobalBranches(IEnumerable<string> allElementBranches)
        {
            var allPotentialParents = new Dictionary<string, HashSet<string>>();
            foreach (var branch in allElementBranches)
            {
                var path = branch.Split('\\');
                if (path.Length <= 1)
                    continue;

                allPotentialParents.AddToCollection(path[path.Length - 1], path[path.Length - 2]);
            }
            var depths = allPotentialParents.Keys.ToDictionary(s => s, unused => 0);
            depths["main"] = 1;
            bool finished = false;
            while (!finished)
            {
                finished = true;
                foreach (var pair in allPotentialParents)
                {
                    int depth = pair.Value.Max(p => depths[p]) + 1;
                    if (depth > depths[pair.Key])
                    {
                        finished = false;
                        depths[pair.Key] = depth;
                    }
                }
            }
            GlobalBranches = new Dictionary<string, string>();
            GlobalBranches["main"] = null;
            foreach (var pair in allPotentialParents)
            {
                var maxDepth = pair.Value.Max(p => depths[p]);
                var candidates = pair.Value.Where(p => depths[p] == maxDepth);
                if (candidates.Count() != 1)
                    throw new Exception("Could not compute parent of branch " + pair.Key + " among " + string.Join(", ", candidates));
                GlobalBranches[pair.Key] = candidates.First();
            }
        }

        private void FilterBranches()
        {
            if (_branchFilters == null || _branchFilters.Count == 0)
                return;
            var branchesToRemove = new HashSet<string>(GlobalBranches.Keys.Where(b => b != "main" && !_branchFilters.Exists(r => r.IsMatch(b))));
            bool finished = false;
            while (!finished)
            {
                finished = true;
                foreach (var toRemove in branchesToRemove)
                {
                    // only branches from which no non-filtered branches spawn can be removed
                    if (GlobalBranches.ContainsKey(toRemove) && !GlobalBranches.Values.Contains(toRemove))
                    {
                        Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet, "Branch " + toRemove + " filtered out");
                        finished = false;
                        GlobalBranches.Remove(toRemove);
                        _changeSets.Remove(toRemove);
                    }
                }
            }
        }

        private void FilterLabels()
        {
            var labelsToRemove = Labels.Values
                .Where(l => l.Versions.Exists(v => !GlobalBranches.ContainsKey(v.Branch.BranchName)))
                .Select(l => l.Name).ToList();
            foreach (var toRemove in labelsToRemove)
            {
                Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet, "Label " + toRemove + " filtered : was on a filtered out branch");
                Labels.Remove(toRemove);
            }
            foreach (var label in Labels.Values)
                label.Reset();
        }
    }
}
