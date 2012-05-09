using System;
using System.Diagnostics;
using System.IO;
using ProtoBuf;

namespace GitImporter
{
    public enum TraceId
    {
        ReadExport = 1,
        ReadCleartool,
        CreateChangeSet,
        ApplyChangeSet,
        Cleartool
    }

    class Program
    {
        public static TraceSource Logger = new TraceSource("GitImporter", SourceLevels.All);

        static void Main(string[] args)
        {
            Console.Error.WriteLine("GitImporter called with {0} arguments :", args.Length);
            foreach (string arg in args)
                Console.Error.WriteLine("    " + arg);
            var importerArguments = new ImporterArguments();
            if (!CommandLine.Parser.ParseArgumentsWithUsage(args, importerArguments))
                return;
            if (!importerArguments.CheckArguments())
            {
                Console.Error.WriteLine(CommandLine.Parser.ArgumentsUsage(typeof(ImporterArguments)));
                return;
            }

            try
            {
                Logger.TraceData(TraceEventType.Start | TraceEventType.Information, 0, "Start program");
                VobDB vobDB = null;

                if (!string.IsNullOrEmpty(importerArguments.FetchFileContent))
                {
                    using (var gitWriter = new GitWriter(importerArguments.ClearcaseRoot, importerArguments.NoFileContent))
                    {
                        if (File.Exists(importerArguments.ThirdpartyConfig))
                        {
                            var thirdPartyConfig = ThirdPartyConfig.ReadFromFile(importerArguments.ThirdpartyConfig);
                            var hook = new ThirdPartyHook(thirdPartyConfig);
                            gitWriter.PreWritingHooks.AddRange(hook.PreWritingHooks);
                            gitWriter.PostWritingHooks.AddRange(hook.PostWritingHooks);
                        }
                        gitWriter.WriteFile(importerArguments.FetchFileContent);
                    }
                    Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, 0, "Stop program");
                    return;
                }
                if (importerArguments.LoadVobDB != null && importerArguments.LoadVobDB.Length > 0)
                {
                    foreach (string vobDBFile in importerArguments.LoadVobDB)
                    {
                        using (var stream = new FileStream(vobDBFile, FileMode.Open))
                            if (vobDB == null)
                                vobDB = Serializer.Deserialize<VobDB>(stream);
                            else
                                vobDB.Add(Serializer.Deserialize<VobDB>(stream));
                        Logger.TraceData(TraceEventType.Information, 0, "Clearcase data successfully loaded from " + vobDBFile);
                    }
                }

                var exportReader = new ExportReader();
                foreach (var file in importerArguments.ExportFiles)
                    exportReader.ReadFile(file);

                if (!string.IsNullOrWhiteSpace(importerArguments.DirectoriesFile) ||
                    !string.IsNullOrWhiteSpace(importerArguments.ElementsFile) ||
                    !string.IsNullOrWhiteSpace(importerArguments.VersionsFile))
                    using (var cleartoolReader = new CleartoolReader(importerArguments.ClearcaseRoot, importerArguments.OriginDate))
                    {
                        cleartoolReader.Init(vobDB, exportReader.Elements);
                        // first save of exportReader with oid (if something was actually read)
                        vobDB = cleartoolReader.VobDB;
                        if (importerArguments.ExportFiles.Length > 0 && !string.IsNullOrWhiteSpace(importerArguments.SaveVobDB))
                        {
                            using (var stream = new FileStream(importerArguments.SaveVobDB + ".export_oid", FileMode.Create))
                                Serializer.Serialize(stream, vobDB);
                            Logger.TraceData(TraceEventType.Information, 0, "Clearcase export with oid successfully saved in " + importerArguments.SaveVobDB + ".export_oid");
                        }
                        cleartoolReader.Read(importerArguments.DirectoriesFile, importerArguments.ElementsFile, importerArguments.VersionsFile);
                        vobDB = cleartoolReader.VobDB;
                        if (!string.IsNullOrWhiteSpace(importerArguments.SaveVobDB))
                        {
                            using (var stream = new FileStream(importerArguments.SaveVobDB, FileMode.Create))
                                Serializer.Serialize(stream, vobDB);
                            Logger.TraceData(TraceEventType.Information, 0, "Clearcase data successfully saved in " + importerArguments.SaveVobDB);
                        }
                    }

                if (!importerArguments.GenerateVobDBOnly)
                {
                    var historyBuilder = new HistoryBuilder(vobDB);
                    historyBuilder.SetRoots(importerArguments.Roots);
                    historyBuilder.SetBranchFilters(importerArguments.Branches);
                    var changeSets = historyBuilder.Build();

                    using (var gitWriter = new GitWriter(importerArguments.ClearcaseRoot, importerArguments.NoFileContent))
                    {
                        if (File.Exists(importerArguments.IgnoreFile))
                            gitWriter.InitialFiles.Add(new Tuple<string, string>(".gitignore", importerArguments.IgnoreFile));
                        if (File.Exists(importerArguments.ThirdpartyConfig))
                        {
                            var thirdPartyConfig = ThirdPartyConfig.ReadFromFile(importerArguments.ThirdpartyConfig);
                            var hook = new ThirdPartyHook(thirdPartyConfig);
                            gitWriter.PreWritingHooks.AddRange(hook.PreWritingHooks);
                            gitWriter.PostWritingHooks.AddRange(hook.PostWritingHooks);
                            gitWriter.InitialFiles.Add(new Tuple<string, string>(".gitmodules", hook.ModulesFile));
                        }
                        gitWriter.WriteChangeSets(changeSets);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.TraceData(TraceEventType.Critical, 0, "Exception during import : " + ex);
                Console.Error.WriteLine("Exception during import : " + ex);
            }
            finally
            {
                Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, 0, "Stop program");
                Logger.Flush();
            }
        }
    }
}
