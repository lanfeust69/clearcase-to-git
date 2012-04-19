using System;
using System.Collections.Generic;
using System.Diagnostics;

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
            ProcessElementNames();
            return _flattenChangeSets;
        }

        private void ProcessElementNames()
        {
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Start process element names");
            int n = 0;

            var startedBranches = new HashSet<string>();
            var branchTips = new Dictionary<string, ChangeSet>();
            // an element may appear under different names, especially during a move,
            // if the destination directory has been checked in before source directory
            var elementsNamesByBranch = new Dictionary<string, Dictionary<Element, HashSet<string>>>();
            // branch and version for which the elementName could not be found
            var orphanedVersionsByElement = new Dictionary<Element, List<Tuple<string, ChangeSet.NamedVersion>>>();
            // some moves (rename) may be in separate ChangeSets, we must be able to know what version to write at the new location
            var elementsVersionsByBranch = new Dictionary<string, Dictionary<Element, ElementVersion>>();
            foreach (var changeSet in _flattenChangeSets)
            {
                n++;
                changeSet.Id = n;
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

                foreach (var namedVersion in changeSet.Versions)
                    changeSet.Labels.AddRange(ProcessLabels(namedVersion.Version, elementsVersions));

                var changeSetBuilder = new ChangeSetBuilder(changeSet, elementsNames, elementsVersions, orphanedVersionsByElement, _roots);
                changeSetBuilder.Build();

                Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Stop process element names in ChangeSet", changeSet.Id);
            }

            // really lost versions
            foreach (var orphanedVersions in orphanedVersionsByElement.Values)
                foreach (var orphanedVersion in orphanedVersions)
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                     "Version " + orphanedVersion.Item2.Version + " has not been visible in any imported directory version");

            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Stop process element names");
        }

        private IEnumerable<string> ProcessLabels(ElementVersion version, Dictionary<Element, ElementVersion> elementsVersions)
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
                // so we removed the last missing version, check that everything is still OK
                _labels.Remove(label);
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
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                        "Label " + label + " was inconsistent : not applied");
                else
                    result.Add(label);
            }
            return result;
        }
    }
}
