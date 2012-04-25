using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace GitImporter
{
    public class HistoryBuilder
    {
        public static TraceSource Logger = Program.Logger;

        private readonly RawHistoryBuilder _rawHistoryBuilder;

        /// <summary>
        /// _roots are directory whose parents have not been requested :
        /// they will therefore never appear in the Content of a DirectoryVersion
        /// </summary>
        private readonly HashSet<string> _roots = new HashSet<string>();

        /// <summary>
        /// For each branch, its parent branch
        /// </summary>
        private Dictionary<string, string> _globalBranches;
        private Dictionary<string, LabelInfo> _labels;
        private readonly HashSet<string> _ignoredLabels = new HashSet<string>();

        private List<ChangeSet> _flattenChangeSets;

        public HistoryBuilder(VobDB vobDB)
        {
            _rawHistoryBuilder = new RawHistoryBuilder(vobDB);
        }

        public void SetBranchFilters(string[] branches)
        {
            _rawHistoryBuilder.SetBranchFilters(branches);
        }

        public void SetRoots(IEnumerable<string> roots)
        {
            _roots.Clear();
            foreach (var root in roots)
                _roots.Add(root);
        }

        public IList<ChangeSet> Build()
        {
            _flattenChangeSets = _rawHistoryBuilder.Build();
            _globalBranches = _rawHistoryBuilder.GlobalBranches;
            _labels = _rawHistoryBuilder.Labels;
            return ProcessElementNames();
        }

        private List<ChangeSet> ProcessElementNames()
        {
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Start process element names");
            int n = 0;

            // same content than _flattenChangeSets, but not necessarily same order
            var result = new List<ChangeSet>(_flattenChangeSets.Count);

            var startedBranches = new HashSet<string>();
            var branchTips = new Dictionary<string, ChangeSet>();
            // an element may appear under different names, especially during a move,
            // if the destination directory has been checked in before source directory
            var elementsNamesByBranch = new Dictionary<string, Dictionary<Element, HashSet<string>>>();
            // branch and version for which the elementName could not be found
            var orphanedVersionsByElement = new Dictionary<Element, List<Tuple<string, ChangeSet.NamedVersion>>>();
            // some moves (rename) may be in separate ChangeSets, we must be able to know what version to write at the new location
            var elementsVersionsByBranch = new Dictionary<string, Dictionary<Element, ElementVersion>>();

            int nextChangeSetIndex = 0;
            var delayedChangeSets = new List<Tuple<ChangeSet, HashSet<string>, List<ChangeSet>>>();
            var delayingLabels = new Dictionary<string, int>();
            var applyNowChangeSets = new Queue<ChangeSet>();
            int maxDelayPerLabel = 20;
            int maxNbDelayed = 50;
            while (true)
            {
                // do not delay too many times on account of the same label :
                // it may be an inconsistent one, and holding up changeSets may prevent good labels to be applied
                var badLabels = delayingLabels.Where(p => p.Value > maxDelayPerLabel).Select(p => p.Key).ToList();
                foreach (var badLabel in badLabels)
                {
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet, "Label " + badLabel + " delayed too many change sets : ignored");
                    _ignoredLabels.Add(badLabel);
                    delayingLabels.Remove(badLabel);
                }
                foreach (var delayed in delayedChangeSets.ToList())
                {
                    foreach (var badLabel in badLabels)
                        delayed.Item2.Remove(badLabel);
                    if (delayed.Item2.Count == 0)
                    {
                        // no need to check dependencies here : they were in the list before, with a subset of labels
                        applyNowChangeSets.Enqueue(delayed.Item1);
                        delayedChangeSets.Remove(delayed);
                    }
                }

                ChangeSet changeSet;
                if (applyNowChangeSets.Count > 0)
                    changeSet = applyNowChangeSets.Dequeue();
                else if (nextChangeSetIndex == _flattenChangeSets.Count)
                {
                    if (delayedChangeSets.Count == 0)
                        // done !
                        break;
                    foreach (var delayed in delayedChangeSets)
                        applyNowChangeSets.Enqueue(delayed.Item1);
                    delayedChangeSets.Clear();
                    continue;
                }
                else if (delayedChangeSets.Count > maxNbDelayed)
                {
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet, "Too many change sets delayed : forcing");
                    changeSet = delayedChangeSets[0].Item1;
                    delayedChangeSets.RemoveAt(0);
                }
                else
                {
                    changeSet = _flattenChangeSets[nextChangeSetIndex++];
                    bool delayed = DelayIfUseful(changeSet, delayedChangeSets, applyNowChangeSets, delayingLabels);
                    if (delayed || applyNowChangeSets.Count > 0)
                        continue;
                }

                n++;
                changeSet.Id = n;
                result.Add(changeSet);
                Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Start process element names in ChangeSet", changeSet);
                if (n % 1000 == 0)
                    Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet, "Processing element names in ChangeSet", changeSet);

                branchTips[changeSet.Branch] = changeSet;
                Dictionary<Element, HashSet<string>> elementsNames;
                Dictionary<Element, ElementVersion> elementsVersions;
                bool isNewBranch = !startedBranches.Contains(changeSet.Branch);
                if (isNewBranch)
                {
                    // n is 1-based index because it is used as a mark id for git fast-import, that reserves id 0
                    if (changeSet.Branch != "main")
                    {
                        string parentBranch = _globalBranches[changeSet.Branch];
                        changeSet.BranchingPoint = branchTips[parentBranch];
                        branchTips[parentBranch].IsBranchingPoint = true;
                        elementsNames = new Dictionary<Element, HashSet<string>>(elementsNamesByBranch[parentBranch]);
                        elementsVersions = new Dictionary<Element, ElementVersion>(elementsVersionsByBranch[parentBranch]);
                    }
                    else
                    {
                        elementsNames = new Dictionary<Element, HashSet<string>>();
                        elementsVersions = new Dictionary<Element, ElementVersion>();
                    }
                    elementsNamesByBranch.Add(changeSet.Branch, elementsNames);
                    elementsVersionsByBranch.Add(changeSet.Branch, elementsVersions);
                    startedBranches.Add(changeSet.Branch);
                }
                else
                {
                    elementsNames = elementsNamesByBranch[changeSet.Branch];
                    elementsVersions = elementsVersionsByBranch[changeSet.Branch];
                }

                var changeSetBuilder = new ChangeSetBuilder(changeSet, elementsNames, elementsVersions, orphanedVersionsByElement, _roots);
                changeSetBuilder.Build();

                var finishedLabels = new HashSet<string>();
                foreach (var namedVersion in changeSet.Versions)
                    changeSet.Labels.AddRange(ProcessLabels(namedVersion.Version, elementsVersions, finishedLabels));
                // maybe some delayed changeSets are available now (iterate on a copy to be able to remove)
                if (finishedLabels.Count > 0)
                {
                    foreach (var label in finishedLabels)
                    {
                        delayingLabels.Remove(label);
                        _ignoredLabels.Remove(label);
                    }

                    foreach (var delayed in delayedChangeSets.ToList())
                    {
                        foreach (string label in finishedLabels)
                            delayed.Item2.Remove(label);
                        if (delayed.Item2.Count == 0)
                        {
                            // no need to check dependencies here : they were in the list before, with a subset of labels
                            applyNowChangeSets.Enqueue(delayed.Item1);
                            delayedChangeSets.Remove(delayed);
                        }
                    }
                }

                Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Stop process element names in ChangeSet", changeSet.Id);
            }

            // really lost versions
            foreach (var orphanedVersions in orphanedVersionsByElement.Values)
                foreach (var orphanedVersion in orphanedVersions)
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                     "Version " + orphanedVersion.Item2.Version + " has not been visible in any imported directory version");

            // uncompleted labels
            foreach (var label in _labels.Values)
            {
                foreach (var missingVersion in label.MissingVersions)
                    Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet,
                                     "Version " + missingVersion + " with label " + label.Name + " not seen");
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet, "Label " + label.Name + " has " + label.MissingVersions.Count + " missing versions : not applied");
            }

            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Stop process element names");
            
            return result;
        }

        private bool DelayIfUseful(ChangeSet changeSet,
            List<Tuple<ChangeSet, HashSet<string>, List<ChangeSet>>> delayedChangeSets,
            Queue<ChangeSet> applyNowChangeSets, Dictionary<string, int> delayingLabels)
        {
            var changedBranches = new HashSet<ElementBranch>(changeSet.Versions.Select(v => v.Version.Branch));
            var branchingPoints = changeSet.Versions
                .Where(v => v.Version.VersionNumber == 0 && v.Version.Branch.BranchingPoint != null)
                .Select(v => v.Version.Branch.BranchingPoint).ToList();

            // dependencies are versions on the same branch, or the branching point of a version 0
            var dependencies = delayedChangeSets
                .Where(delayed => delayed.Item1.Versions.Select(v => v.Version)
                            .Any(v =>
                                changedBranches.Contains(v.Branch) ||
                                branchingPoints.Any(bp => bp.Branch == v.Branch && bp.VersionNumber >= v.VersionNumber)))
                .ToList();

            var wouldBreakLabels = new HashSet<string>(dependencies.SelectMany(d => d.Item2).Union(FindWouldBreakLabels(changeSet)));

            if (dependencies.Count == 0 && wouldBreakLabels.Count == 0)
                return false;

            // if any waiting label is on one of the currenty changeSet's versions, apply it (and the dependencies)
            // TODO : this may not be optimal
            if (changeSet.Versions.SelectMany(v => v.Version.Labels)
                .Any(label => delayingLabels.ContainsKey(label) || wouldBreakLabels.Contains(label)))
            {
                Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "ChangeSet " + changeSet + " needed for a waiting label : not delayed");
                var allDependencies = GetAllDependencies(dependencies.Select(d => d.Item1), delayedChangeSets.ToDictionary(t => t.Item1, t => t.Item3));
                // loop again over delayedChangeSets to keep correct order
                foreach (var dependency in delayedChangeSets.ToList())
                    if (allDependencies.Contains(dependency.Item1))
                    {
                        applyNowChangeSets.Enqueue(dependency.Item1);
                        delayedChangeSets.Remove(dependency);
                    }
                applyNowChangeSets.Enqueue(changeSet);
            }
            else
            {
                Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet,
                    "ChangeSet " + changeSet + " delayed for labels " + string.Join(", ", wouldBreakLabels));
                delayedChangeSets.Add(new Tuple<ChangeSet, HashSet<string>, List<ChangeSet>>(changeSet, wouldBreakLabels, dependencies.Select(d => d.Item1).ToList()));
                foreach (var label in wouldBreakLabels)
                {
                    int count;
                    delayingLabels.TryGetValue(label, out count);
                    delayingLabels[label] = count + 1;
                }
            }

            return true;
        }

        private static HashSet<ChangeSet> GetAllDependencies(IEnumerable<ChangeSet> dependencies, Dictionary<ChangeSet, List<ChangeSet>> changeSets)
        {
            var result = new HashSet<ChangeSet>(dependencies);
            foreach (var dependency in dependencies)
            {
                List<ChangeSet> subDependencies;
                if (!changeSets.TryGetValue(dependency, out subDependencies))
                    continue;
                changeSets.Remove(dependency);
                foreach (var subDependency in GetAllDependencies(subDependencies, changeSets))
                    result.Add(subDependency);
            }

            return result;
        }

        private IEnumerable<string> FindWouldBreakLabels(ChangeSet changeSet)
        {
            return changeSet.Versions
                .Select(v => v.Version).Where(v => v.VersionNumber > 0)
                .SelectMany(v => v.GetPreviousVersion().Labels)
                .Where(label => _labels.ContainsKey(label) && !_ignoredLabels.Contains(label));
        }

        private IEnumerable<string> ProcessLabels(ElementVersion version,
            Dictionary<Element, ElementVersion> elementsVersions, HashSet<string> finishedLabels)
        {
            var result = new List<string>();
            foreach (var label in version.Labels)
            {
                LabelInfo labelInfo;
                if (!_labels.TryGetValue(label, out labelInfo))
                    continue;
                labelInfo.MissingVersions.Remove(version);
                if (labelInfo.MissingVersions.Count > 0)
                    continue;
                Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Label " + label + " completed with version " + version);
                // so we removed the last missing version, check that everything is still OK
                _labels.Remove(label);
                finishedLabels.Add(label);
                bool ok = true;
                foreach (var toCheck in labelInfo.Versions)
                {
                    ElementVersion inCurrentVersions;
                    elementsVersions.TryGetValue(toCheck.Element, out inCurrentVersions);
                    if ((inCurrentVersions == null && toCheck.VersionNumber != 0) ||
                        (inCurrentVersions != null && inCurrentVersions != toCheck))
                    {
                        string msg = "Label " + label + " is inconsistent : should be on " + toCheck +
                            (inCurrentVersions == null ? ", but element has no current version" : ", not on " + inCurrentVersions);
                        Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet, msg);
                        ok = false;
                    }
                }
                if (!ok)
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet, "Label " + label + " was inconsistent : not applied");
                else
                {
                    Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Label " + label + " was applied");
                    result.Add(label);
                }
            }
            return result;
        }
    }
}
