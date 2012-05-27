using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GitImporter
{
    public class ThirdPartyHook
    {
        private readonly Regex _thirdPartyRegex;
        private readonly Dictionary<string, Dictionary<string, string>> _thirdPartyModules = new Dictionary<string, Dictionary<string, string>>();
        private readonly List<Tuple<Regex, string>> _specificRules = new List<Tuple<Regex, string>>();
        private readonly List<Tuple<string, string>> _projectFileMappings = new List<Tuple<string, string>>();

        private readonly string _thirdpartyRoot;
        private readonly List<Tuple<string, string>> _allModules = new List<Tuple<string, string>>();
        private readonly Dictionary<string, string> _alternateCase = new Dictionary<string, string>();

        private string _gitModulesFile;
        private readonly Dictionary<string, HashSet<string>> _missingLabels = new Dictionary<string, HashSet<string>>();

        public List<GitWriter.PreWritingHook> PreWritingHooks { get; private set; }
        public List<GitWriter.PostWritingHook> PostWritingHooks { get; private set; }

        public string ModulesFile { get { return _gitModulesFile ?? (_gitModulesFile = CreateGitModulesFile()); } }

        public ThirdPartyHook(ThirdPartyConfig config)
        {
            _thirdPartyRegex = new Regex(config.ThirdPartyRegex);
            _thirdpartyRoot = config.Root ?? "";
            foreach (var module in config.Modules)
            {
                string url = config.GitUrl + "/" + module.Name;
                _allModules.Add(new Tuple<string, string>(module.Name, url));
                var labels = module.Labels.ToDictionary(l => l.Label, l => l.Commit);
                _thirdPartyModules.Add(module.Name, labels);
                if (module.AlternateNames != null)
                    foreach (var alternateName in module.AlternateNames)
                    {
                        // only the standard name when case-only difference
                        if (string.Compare(alternateName, module.Name, true) != 0)
                        {
                            _thirdPartyModules.Add(alternateName, labels);
                            _allModules.Add(new Tuple<string, string>(alternateName, url));
                        }
                        else
                            _alternateCase.Add(alternateName, module.Name);
                    }
                if (!string.IsNullOrEmpty(module.ConfigSpecRegex))
                    _specificRules.Add(new Tuple<Regex, string>(new Regex(module.ConfigSpecRegex), module.Name));
                if (module.ProjectFileMappings != null)
                    _projectFileMappings.AddRange(module.ProjectFileMappings.Select(m => new Tuple<string, string>(m.From, m.To)));
            }

            PreWritingHooks = new List<GitWriter.PreWritingHook>
                { new GitWriter.PreWritingHook(new Regex(config.ProjectFileRegex), ProcessProjectFile) };
            PostWritingHooks = new List<GitWriter.PostWritingHook>
                { new GitWriter.PostWritingHook(new Regex(config.ConfigSpecRegex), ProcessConfigSpec) };
        }

        private string CreateGitModulesFile()
        {
            string tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            using (var writer = new StreamWriter(tmp))
            {
                foreach (var module in _allModules)
                {
                    writer.Write("[submodule \"" + _thirdpartyRoot + module.Item1 + "\"]\n");
                    writer.Write("\tpath = " + _thirdpartyRoot + module.Item1 + "\n");
                    writer.Write("\turl = " + module.Item2 + "\n");
                }
            }
            const string baseName = "gitmodules";
            string fileName = baseName;
            int n = 0;
            string tmpHash = null;
            while (File.Exists(fileName))
            {
                if (tmpHash == null)
                    tmpHash = GetFileHash(tmp);
                string existingHash = GetFileHash(fileName);
                if (tmpHash == existingHash)
                {
                    File.Delete(tmpHash);
                    return fileName;
                }
                fileName = baseName + "." + ++n;
            }
            File.Move(tmp, fileName);
            return fileName;
        }
        
        private static string GetFileHash(string filePath)
        {
            byte[] hash = new MD5CryptoServiceProvider().ComputeHash(File.ReadAllBytes(filePath));
            var sBuilder = new StringBuilder();
            foreach (byte b in hash)
            {
                sBuilder.Append(b.ToString("x2").ToLower());
            }
            return sBuilder.ToString();
        }

        public void ProcessConfigSpec(string configSpec, StreamWriter writer)
        {
            string line;
            using (var stream = new StreamReader(configSpec))
                while ((line = stream.ReadLine()) != null)
                {
                    bool ruleFound = false;
                    string module = null;
                    string label = null;
                    Match match;
                    foreach (var rule in _specificRules)
                    {
                        match = rule.Item1.Match(line);
                        if (!match.Success)
                            continue;
                        module = rule.Item2;
                        label = match.Groups[1].Value;
                        ruleFound = true;
                        break;
                    }
                    if (!ruleFound)
                    {
                        match = _thirdPartyRegex.Match(line);
                        if (!match.Success)
                            continue;
                        module = match.Groups[1].Value;
                        label = match.Groups[2].Value;
                    }
                    Dictionary<string, string> dict;
                    string standardCaseModule;
                    if (!_alternateCase.TryGetValue(module, out standardCaseModule))
                        standardCaseModule = module;
                    if (!_thirdPartyModules.TryGetValue(standardCaseModule, out dict))
                        continue;
                    string commit;
                    if (!dict.TryGetValue(label, out commit))
                    {
                        HashSet<string> missing;
                        if (!_missingLabels.TryGetValue(standardCaseModule, out missing))
                        {
                            missing = new HashSet<string>();
                            _missingLabels.Add(standardCaseModule, missing);
                        }
                        if (!missing.Contains(label))
                        {
                            missing.Add(label);
                            GitWriter.Logger.TraceData(TraceEventType.Warning, (int)TraceId.ApplyChangeSet, "label " + label + " not found for module " + standardCaseModule);
                        }
                        continue;
                    }

                    writer.Write("M 160000 " + commit + " " + _thirdpartyRoot + standardCaseModule + "\n");
                }
        }

        public void ProcessProjectFile(string file)
        {
            File.Move(file, file + ".bak");
            string line;
            using (var reader = new StreamReader(file + ".bak"))
            using (var writer = new StreamWriter(file))
                while ((line = reader.ReadLine()) != null)
                {
                    foreach (var mapping in _projectFileMappings)
                        line = line.Replace(mapping.Item1, mapping.Item2);
                    writer.WriteLine(line);
                }
            File.Delete(file + ".bak");
        }
    }
}
