using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;

using CommandLine;

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
                Console.WriteLine(CommandLine.Parser.ArgumentsUsage(typeof(ImporterArguments)));
                return;
            }

            try
            {
                Logger.TraceData(TraceEventType.Start | TraceEventType.Information, 0, "Start program");
                VobDB vobDB;

                if (!string.IsNullOrWhiteSpace(importerArguments.LoadVobDB))
                {
                    var formatter = new BinaryFormatter();
                    using (var stream = new FileStream(importerArguments.LoadVobDB, FileMode.Open))
                        vobDB = (VobDB)formatter.Deserialize(stream);
                    Logger.TraceData(TraceEventType.Information, 0, "Clearcase data successfully loaded from " + importerArguments.LoadVobDB);
                }
                else
                {
                    var exportReader = new ExportReader();
                    foreach (var file in importerArguments.ExportFiles)
                        exportReader.ReadFile(file);

                    using (var cleartoolReader = new CleartoolReader())
                    {
                        cleartoolReader.Init(exportReader.Elements);
                        cleartoolReader.Read(importerArguments.DirectoriesFile, importerArguments.ElementsFile);
                        vobDB = new VobDB(cleartoolReader.DirectoryElements, cleartoolReader.FileElements, cleartoolReader.ElementsByOid);
                        if (!string.IsNullOrWhiteSpace(importerArguments.SaveVobDB))
                        {
                            var formatter = new BinaryFormatter();
                            using (var stream = new FileStream(importerArguments.SaveVobDB, FileMode.Create))
                                formatter.Serialize(stream, vobDB);
                            Logger.TraceData(TraceEventType.Information, 0, "Clearcase data successfully saved in " + importerArguments.SaveVobDB);
                        }
                    }
                }

                if (!importerArguments.GenerateVobDBOnly)
                {
                    ChangeSetBuilder changeSetBuilder = new ChangeSetBuilder(vobDB);
                    changeSetBuilder.SetBranchFilters(importerArguments.Branches);
                    changeSetBuilder.Build();
                }

                Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, 0, "Stop program");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception during import : " + ex);
            }
        }
    }
}
