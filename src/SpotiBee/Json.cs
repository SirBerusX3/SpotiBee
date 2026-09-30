using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SpotiBee
{
    internal static class Json
    {
        public static T Parse<T>(string json)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }

        public static bool TryParse<T>(string json, out T result)
        {
            try
            {
                result = Parse<T>(json);
                return result != null;
            }
            catch
            {
                result = default;
                return false;
            }
        }

        public static string Stringify<T>(T value)
        {
            using var stream = new MemoryStream();
            new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
