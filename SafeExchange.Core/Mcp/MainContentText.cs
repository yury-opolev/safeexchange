/// <summary>
/// MainContentText
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using AngleSharp.Dom;
    using AngleSharp.Html.Parser;
    using System;
    using System.Net;
    using System.Text;

    /// <summary>
    /// A secret's main content is rich text (HTML, sanitised on upload). MCP tools exchange plain text, so
    /// <see cref="ToHtml"/> writes each line as an encoded paragraph (the shape the web editor produces) and
    /// <see cref="ToText"/> reads paragraphs, line breaks and other blocks back as lines.
    /// </summary>
    public static class MainContentText
    {
        private const string EmptyParagraph = "<p><br></p>";

        public static string ToHtml(string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            var builder = new StringBuilder();
            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                builder.Append(line.Length == 0 ? EmptyParagraph : $"<p>{WebUtility.HtmlEncode(line)}</p>");
            }

            return builder.ToString();
        }

        public static string ToText(string html)
        {
            ArgumentNullException.ThrowIfNull(html);

            var document = new HtmlParser().ParseDocument(html);
            var builder = new StringBuilder();
            if (document.Body is not null)
            {
                AppendNode(document.Body, builder);
            }

            if (builder.Length > 0 && builder[^1] == '\n')
            {
                builder.Length--;
            }

            return builder.ToString();
        }

        private static void AppendNode(INode node, StringBuilder builder)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child is IText text)
                {
                    builder.Append(text.Data);
                    continue;
                }

                if (child is not IElement element)
                {
                    continue;
                }

                if (element.LocalName == "br")
                {
                    builder.Append('\n');
                    continue;
                }

                AppendNode(element, builder);
                if (IsBlock(element.LocalName) && builder.Length > 0 && builder[^1] != '\n')
                {
                    builder.Append('\n');
                }
            }
        }

        private static bool IsBlock(string localName) => localName switch
        {
            "p" or "div" or "li" or "pre" or "blockquote" or "tr" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => true,
            _ => false,
        };
    }
}
