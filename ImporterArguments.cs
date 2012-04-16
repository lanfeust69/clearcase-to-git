using System;
using CommandLine;

namespace GitImporter
{
    public class ImporterArguments
    {
        [Argument(ArgumentType.AtMostOnce, HelpText = "File in which the complete clearcase data will be saved.")]
        public string SaveVobDB;
        [Argument(ArgumentType.MultipleUnique, HelpText = "Files from which which the complete clearcase data will be loaded.", DefaultValue = new string[0])]
        public string[] LoadVobDB;
        [Argument(ArgumentType.AtMostOnce, HelpText = "File listing directories to import.")]
        public string DirectoriesFile;
        [Argument(ArgumentType.AtMostOnce, HelpText = "File listing (non-directory) elements to import.")]
        public string ElementsFile;
        [Argument(ArgumentType.AtMostOnce, HelpText = "File listing individual versions to import.")]
        public string VersionsFile;
        [Argument(ArgumentType.MultipleUnique, HelpText = "Roots : directory elements whose parents are not imported.", DefaultValue = new[] { "." })]
        public string[] Roots;
        [Argument(ArgumentType.MultipleUnique, HelpText = "Branches to import (may be a regular expression).", DefaultValue = new[] { "^PROD\\d+\\.\\d+" })]
        public string[] Branches;
        [Argument(ArgumentType.AtMostOnce, HelpText = "Full path from which element names are specified (must be within a clearcase view).")]
        public string ClearcaseRoot;
        [Argument(ArgumentType.AtMostOnce, HelpText = "Date up to which versions will be retrieved, more recent are discarded (use to keep consistency between directory contents and elements).")]
        public string OriginDate;
        [Argument(ArgumentType.AtMostOnce, HelpText = "Indicates to stop after having saved clearcase data.")]
        public bool GenerateVobDBOnly;
        [Argument(ArgumentType.AtMostOnce, HelpText = "Indicates not to load file contents from clearcase.", DefaultValue = false)]
        public bool NoFileContent;
        [Argument(ArgumentType.AtMostOnce, HelpText = "File previously generated with -NoFileContent, where content will now be retrieved from clearcase.")]
        public string FetchFileContent;
        [Argument(ArgumentType.AtMostOnce, HelpText = "File that will be added as .gitignore at the repo root.")]
        public string IgnoreFile;
        [DefaultArgument(ArgumentType.MultipleUnique, HelpText = "Export files generated using clearexport. Each file is supposed to be directly in the working directory, but there may be a prefix that means a path from the main clearcase root.")]
        public string[] ExportFiles = new string[0];

        public bool CheckArguments()
        {
            if (GenerateVobDBOnly && string.IsNullOrWhiteSpace(SaveVobDB))
            {
                Console.Error.WriteLine("SaveVobDB file must be specified if GenerateVobDBOnly");
                return false;
            }
            if (string.IsNullOrEmpty(ClearcaseRoot) && (LoadVobDB == null || LoadVobDB.Length == 0) &&
                (!NoFileContent || !string.IsNullOrEmpty(DirectoriesFile) || !string.IsNullOrEmpty(ElementsFile) || !string.IsNullOrEmpty(VersionsFile) || ExportFiles.Length > 0))
            {
                Console.Error.WriteLine("ClearcaseRoot is required except when generating an import file from clearcase data loaded from a file, without actual contents");
                return false;
            }
            if (string.IsNullOrEmpty(FetchFileContent) && (LoadVobDB == null || LoadVobDB.Length == 0) &&
                string.IsNullOrWhiteSpace(DirectoriesFile) && string.IsNullOrWhiteSpace(ElementsFile) && string.IsNullOrWhiteSpace(VersionsFile))
            {
                Console.Error.WriteLine("Either [FetchFileContent], [LoadVobDB] or [at least one from DirectoriesFile, ElementsFile and VersionsFile (and optionally ExportFiles)] must be provided");
                return false;
            }
            if (!string.IsNullOrEmpty(FetchFileContent) && ((LoadVobDB != null && LoadVobDB.Length > 0) || !string.IsNullOrEmpty(SaveVobDB) ||
                !NoFileContent || !string.IsNullOrEmpty(DirectoriesFile) || !string.IsNullOrEmpty(ElementsFile) || !string.IsNullOrEmpty(VersionsFile) || ExportFiles.Length > 0))
            {
                Console.Error.WriteLine("FetchFileContent must be used with ClearcaseRoot and no other option");
                return false;
            }
            DateTime d;
            if (!string.IsNullOrEmpty(OriginDate) && !DateTime.TryParse(OriginDate, out d))
            {
                Console.Error.WriteLine("OriginDate must be parsable as a DateTime");
                return false;
            }

            return true;
        }
    }
}
