using System;
using System.Diagnostics;

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
            try
            {
                Logger.TraceData(TraceEventType.Start | TraceEventType.Information, 0, "Start program");

                using (var cleartool = new Cleartool())
                {
                    Console.WriteLine("pwd : " + cleartool.Pwd());
                    Console.WriteLine("lsvtree :");
                    foreach (var v in cleartool.Lsvtree("fdjg"))
                        Console.WriteLine("    " + v);
                    Console.WriteLine("get : " + cleartool.Get("qsdf"));
                }

                if (false)
                {
                    var exportReader = new ExportReader();
                    foreach (var file in args)
                        exportReader.ReadFile(file);
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
