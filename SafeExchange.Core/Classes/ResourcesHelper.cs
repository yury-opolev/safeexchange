/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using System.IO;
    using System.Reflection;

    public static class ResourcesHelper
    {
        public const string ObjectValueHtmlTemplateName = "SafeExchange.Core.Templates.GetSecret.template.html";

        public static string ReadEmbeddedResource(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();

            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            using (StreamReader reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
