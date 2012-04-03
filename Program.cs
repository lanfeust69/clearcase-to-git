using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;

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

                if (importerArguments.LoadVobDB != null && importerArguments.LoadVobDB.Length > 0)
                {
                    var formatter = new BinaryFormatter();
                    foreach (string vobDBFile in importerArguments.LoadVobDB)
                    {
                        using (var stream = new FileStream(vobDBFile, FileMode.Open))
                            if (vobDB == null)
                                vobDB = (VobDB)formatter.Deserialize(stream);
                            else
                                vobDB.Add((VobDB)formatter.Deserialize(stream));
                        Logger.TraceData(TraceEventType.Information, 0, "Clearcase data successfully loaded from " + vobDBFile);
                    }
                }

                var exportReader = new ExportReader();
                foreach (var file in importerArguments.ExportFiles)
                    exportReader.ReadFile(file);

                if (!string.IsNullOrWhiteSpace(importerArguments.DirectoriesFile) || !string.IsNullOrWhiteSpace(importerArguments.ElementsFile))
                    using (var cleartoolReader = new CleartoolReader(importerArguments.ClearcaseRoot))
                    {
                        cleartoolReader.Init(vobDB, exportReader.Elements);
                        cleartoolReader.Read(importerArguments.DirectoriesFile, importerArguments.ElementsFile);
                        vobDB = cleartoolReader.VobDB;
                        if (!string.IsNullOrWhiteSpace(importerArguments.SaveVobDB))
                        {
                            var formatter = new BinaryFormatter();
                            using (var stream = new FileStream(importerArguments.SaveVobDB, FileMode.Create))
                                formatter.Serialize(stream, vobDB);
                            Logger.TraceData(TraceEventType.Information, 0, "Clearcase data successfully saved in " + importerArguments.SaveVobDB);
                        }
                    }

                if (!importerArguments.GenerateVobDBOnly)
                {
                    var changeSetBuilder = new ChangeSetBuilder(vobDB);
                    changeSetBuilder.SetRoots(importerArguments.Roots);
                    changeSetBuilder.SetBranchFilters(importerArguments.Branches);
                    var changeSets = changeSetBuilder.Build();

                    using (var gitWriter = new GitWriter(importerArguments.ClearcaseRoot, importerArguments.NoFileContent))
                        gitWriter.WriteChangeSets(changeSets);
                }

                Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, 0, "Stop program");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Exception during import : " + ex);
            }
        }
    }
}
