/// <summary>
/// MainContentSanitizer
/// </summary>

namespace SafeExchange.Core.Services
{
    using Ganss.Xss;

    /// <summary>
    /// The HTML policy applied to a secret's main (rich text) content on upload.
    /// </summary>
    internal static class MainContentSanitizer
    {
        public static string Sanitize(string content)
        {
            var sanitizer = new HtmlSanitizer();
            sanitizer.AllowedSchemes.Add("data");
            sanitizer.AllowedAttributes.Add("class");
            sanitizer.AllowedAttributes.Add("data-value");
            sanitizer.AllowedAttributes.Add("data-bs-toggle");
            sanitizer.AllowedAttributes.Add("data-bs-placement");
            sanitizer.AllowedAttributes.Add("title");

            // images-as-attachments spike: keep inline-image geometry/alignment
            // (blotFormatter2 writes width/style) and the attachment reference marker
            // so a stored <img data-saex-attachment="..."> survives upload sanitizing.
            sanitizer.AllowedAttributes.Add("style");
            sanitizer.AllowedAttributes.Add("width");
            sanitizer.AllowedAttributes.Add("height");
            sanitizer.AllowedAttributes.Add("alt");
            sanitizer.AllowedAttributes.Add("data-saex-attachment");

            return sanitizer.Sanitize(content);
        }
    }
}
