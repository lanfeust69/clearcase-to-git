using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace GitImporter
{
    public class ThirdPartyConfig
    {
        public class LabelMapping
        {
            public string Label { get; set; }
            public string Commit { get; set; }
        }
        public class Mapping
        {
            public string From { get; set; }
            public string To { get; set; }
        }
        public class ThirdPartyModule
        {
            public string Name { get; set; }
            public List<string> AlternateNames { get; set; }
            public string ConfigSpecRegex { get; set; }
            public List<Mapping> ProjectFileMappings { get; set; }
            public List<LabelMapping> Labels { get; set; }
        }

        public string Root { get; set; }
        public string GitUrl { get; set; }

        public string ProjectFileRegex { get; set; }
        public string ConfigSpecRegex { get; set; }
        public string ThirdPartyRegex { get; set; }

        public List<ThirdPartyModule> Modules { get; set; }

        public static ThirdPartyConfig ReadFromFile(string configFile)
        {
            var serializer = new XmlSerializer(typeof(ThirdPartyConfig));
            using (var stream = new FileStream(configFile, FileMode.Open))
                return (ThirdPartyConfig)serializer.Deserialize(stream);
        }
    }
}
