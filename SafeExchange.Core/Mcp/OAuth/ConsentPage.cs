/// <summary>
/// ConsentPage
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Text.RegularExpressions;

    /// <summary>
    /// The server-rendered pages of the broker: the consent form (hidden fields, Allow and Deny buttons) and plain
    /// message pages. No JavaScript; every value is HTML-encoded. Serve them with <see cref="SecurityHeaders"/>.
    /// </summary>
    public static class ConsentPage
    {
        public const string Title = "Sign in an MCP client to SafeExchange";

        public const string InvalidRequestMessage = "This sign-in request is invalid or expired. Start the sign-in again from your MCP client.";

        public const string TooManyRequestsMessage = "Too many sign-in requests. Wait a minute, then start the sign-in again from your MCP client.";

        private const string Style = "body{font-family:system-ui,sans-serif;max-width:36rem;margin:3rem auto;padding:0 1rem;line-height:1.5}"
            + "button{font-size:1rem;padding:.5rem 1.25rem;margin-right:.75rem}";

        private static readonly Regex PlainOrigin = new(
            @"\Ahttps?://(?:\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9](?:[A-Za-z0-9.\-]*[A-Za-z0-9])?)(?::[0-9]{1,5})?\z",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// The security headers of every page. Chromium and WebKit apply <c>form-action</c> to the redirects that follow
        /// a form post, so the consent form's own destinations (the Entra authority after Continue, the client's loopback
        /// origin after Cancel) must be listed; error pages have no form and pass no origins.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, string>> SecurityHeaders(IEnumerable<string> formActionOrigins)
            => new KeyValuePair<string, string>[]
            {
                new("Content-Security-Policy", ContentSecurityPolicy(formActionOrigins)),
                new("X-Frame-Options", "DENY"),
                new("X-Content-Type-Options", "nosniff"),
                new("Referrer-Policy", "no-referrer"),
            };

        /// <summary>
        /// <c>form-action</c> is <c>'self'</c> plus each origin, or <c>'none'</c> without origins. An origin that is not
        /// a plain <c>http(s)://host[:port]</c> (no path, user info, space, <c>;</c> or <c>,</c>) is refused, so a value
        /// can never add directives.
        /// </summary>
        public static string ContentSecurityPolicy(IEnumerable<string> formActionOrigins)
        {
            ArgumentNullException.ThrowIfNull(formActionOrigins);
            var origins = formActionOrigins.Distinct(StringComparer.Ordinal).ToList();
            foreach (var origin in origins)
            {
                if (!PlainOrigin.IsMatch(origin))
                {
                    throw new ArgumentException("A form-action origin must be a plain scheme://host[:port].", nameof(formActionOrigins));
                }
            }

            var formAction = origins.Count == 0 ? "'none'" : "'self' " + string.Join(' ', origins);
            return $"default-src 'none'; style-src 'unsafe-inline'; form-action {formAction}; frame-ancestors 'none'; base-uri 'none'";
        }

        /// <param name="formAction">Absolute URL the form posts to (the authorize endpoint).</param>
        /// <param name="clientOrigin"><c>scheme://host:port</c> of the client's redirect URI.</param>
        /// <param name="fields">Hidden form fields, in order.</param>
        public static string Render(string formAction, string clientOrigin, IEnumerable<KeyValuePair<string, string>> fields)
        {
            ArgumentNullException.ThrowIfNull(formAction);
            ArgumentNullException.ThrowIfNull(clientOrigin);
            ArgumentNullException.ThrowIfNull(fields);

            var body = new StringBuilder()
                .Append("<p>An application on this computer is asking for access to SafeExchange as you. It will receive the result at <b>")
                .Append(Encode(clientOrigin))
                .Append("</b>.</p>")
                .Append("<p>Access requested: read and manage your secrets; stay signed in.</p>")
                .Append("<p>Only continue if you just started a sign-in in an MCP client such as Copilot CLI.</p>")
                .Append("<form method=\"post\" action=\"").Append(Encode(formAction)).Append("\">");
            foreach (var field in fields)
            {
                body.Append("<input type=\"hidden\" name=\"").Append(Encode(field.Key)).Append("\" value=\"").Append(Encode(field.Value)).Append("\">");
            }

            body.Append("<button type=\"submit\" name=\"decision\" value=\"allow\">Continue</button>")
                .Append("<button type=\"submit\" name=\"decision\" value=\"deny\">Cancel</button>")
                .Append("</form>");
            return Page(body.ToString());
        }

        public static string Message(string message)
        {
            ArgumentNullException.ThrowIfNull(message);
            return Page("<p>" + Encode(message) + "</p>");
        }

        private static string Page(string body)
            => "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">"
                + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
                + $"<title>{Title}</title><style>{Style}</style></head>"
                + $"<body><main><h1>{Title}</h1>{body}</main></body></html>";

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }
}
