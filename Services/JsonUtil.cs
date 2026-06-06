using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace EpochVisualStudio.Services
{
    /// <summary>
    /// Minimal JSON (de)serialization built on <see cref="DataContractJsonSerializer"/>
    /// so the extension has no third-party dependency. The config and offline-queue
    /// files use the same field names as the VS Code extension, so the two editors
    /// can share a single <c>~/.config/epoch</c> directory.
    /// </summary>
    internal static class JsonUtil
    {
        public static string Serialize<T>(T value)
        {
            using (var ms = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                serializer.WriteObject(ms, value);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static T Deserialize<T>(string json)
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                return (T)serializer.ReadObject(ms);
            }
        }
    }
}
