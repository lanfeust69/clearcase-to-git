using System;
using System.Diagnostics;

namespace GitImporter
{
    public enum TraceId
    {
        ReadExport = 1,
        CreateChangeSet,
        ApplyChangeSet
    }

    class Program
    {
        public static TraceSource Logger = new TraceSource("GitImporter", SourceLevels.All);

        static void Main(string[] args)
        {
            try
            {
                Logger.TraceData(TraceEventType.Start | TraceEventType.Information, 0, "Start program");

                var exportReader = new ExportReader();
                foreach (var file in args)
                    exportReader.ReadFile(file);

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
