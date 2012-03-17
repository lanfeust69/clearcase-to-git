using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CommandLine;

namespace GitImporter
{
    public class ImporterArguments
    {
        [Argument(ArgumentType.Required, HelpText = "File listing directories to import.")]
        public string DirectoriesFile;
        [Argument(ArgumentType.Required, HelpText = "File listing (non-directory) elements to import.")]
        public string ElementsFile;
        [Argument(ArgumentType.MultipleUnique, HelpText = "Branches to import (may be a regular expression).")]
        public string[] Branches = new[] { "PROD.*" };
        [DefaultArgument(ArgumentType.MultipleUnique, HelpText = "Export files generated using clearexport.")]
        public string[] ExportFiles;
    }
}
