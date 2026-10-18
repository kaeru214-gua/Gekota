using System.Collections.Generic;
using System.IO;
using System.Xml;
using Gekota.Flv.Xml;

namespace Gekota.Flv.Tests
{
    internal static class TestTagsExtensions
    {
        public static string SerializeXml(this List<Tag> tags)
        {
            using var sw = new StringWriter();
            using var writer = XmlWriter.Create(sw, new()
            {
                Indent = true
            });

            XmlFlvFile.Serializer.Serialize(writer, new XmlFlvFile { Tags = tags });
            writer.Flush();
            return sw.ToString();
        }
    }
}
