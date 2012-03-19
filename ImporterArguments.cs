using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CommandLine;

namespace GitImporter
{
    public class ImporterArguments
    {
        [Argument(ArgumentType.AtMostOnce, HelpText = "File in which the complete clearcase data will be saved.")]
        public string SaveVobDB;
        [Argument(ArgumentType.AtMostOnce, HelpText = "Indicates to stop after having saved clearcase data.")]
        public bool GenerateVobDBOnly;
        [Argument(ArgumentType.AtMostOnce, HelpText = "File from which which the complete clearcase data will be loaded.")]
        public string LoadVobDB;
        [Argument(ArgumentType.AtMostOnce, HelpText = "File listing directories to import.")]
        public string DirectoriesFile;
        [Argument(ArgumentType.AtMostOnce, HelpText = "File listing (non-directory) elements to import.")]
        public string ElementsFile;
        [Argument(ArgumentType.MultipleUnique, HelpText = "Branches to import (may be a regular expression).", DefaultValue = new[] { "PROD\\d+\\.\\d+" })]
        public string[] Branches;
        [DefaultArgument(ArgumentType.MultipleUnique, HelpText = "Export files generated using clearexport.")]
        public string[] ExportFiles = new string[0];

        public bool CheckArguments()
        {
            if (GenerateVobDBOnly && string.IsNullOrWhiteSpace(SaveVobDB))
            {
                Console.WriteLine("SaveVobDB file must be specified if GenerateVobDBOnly");
                return false;
            }
            if (!string.IsNullOrWhiteSpace(SaveVobDB) && !string.IsNullOrWhiteSpace(LoadVobDB))
            {
                Console.WriteLine("SaveVobDB and LoadVobDB are incompatible");
                return false;
            }
            if ((!string.IsNullOrWhiteSpace(LoadVobDB) &&
                    (!string.IsNullOrWhiteSpace(DirectoriesFile) || !string.IsNullOrWhiteSpace(ElementsFile) || ExportFiles.Length > 0)) ||
                (string.IsNullOrWhiteSpace(LoadVobDB) &&
                    (string.IsNullOrWhiteSpace(DirectoriesFile) || string.IsNullOrWhiteSpace(ElementsFile))))
            {
                Console.WriteLine("Either [LoadVobDB] or [DirectoriesFile, ElementsFile and optionally ExportFiles] must be provided");
                return false;
            }
            return true;
        }
    }
}
