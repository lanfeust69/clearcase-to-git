using System;
using System.Diagnostics;

using CommandLine;
using System.IO;

namespace GitImporter
{
    public enum TraceId
    {
        ReadExport = 1,
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

            try
            {
                Logger.TraceData(TraceEventType.Start | TraceEventType.Information, 0, "Start program");

                var exportReader = new ExportReader();
                foreach (var file in importerArguments.ExportFiles)
                    exportReader.ReadFile(file);

                using (var cleartoolReader = new CleartoolReader())
                {
                    cleartoolReader.Init(exportReader.Elements);
                    using (var directories = new StreamReader(importerArguments.DirectoriesFile))
                    {
                        string line;
                        while ((line = directories.ReadLine()) != null)
                            cleartoolReader.ReadDirectory(line);
                    }
                    using (var directories = new StreamReader(importerArguments.ElementsFile))
                    {
                        string line;
                        while ((line = directories.ReadLine()) != null)
                            cleartoolReader.ReadElement(line);
                    }
                }

                Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, 0, "Stop program");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception during import : " + ex);
            }
            Console.ReadKey();
        }
    }
}
