/// <summary>
/// ConsentForm
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Text.RegularExpressions;

    /// <summary>
    /// What a browser does with the consent page: reads its hidden fields and posts them back with a decision.
    /// </summary>
    public static class ConsentForm
    {
        public static List<KeyValuePair<string, string>> HiddenFields(string? html)
            => Regex.Matches(html ?? string.Empty, "<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\">")
                .Select(m => new KeyValuePair<string, string>(WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)))
                .ToList();

        public static string Post(IEnumerable<KeyValuePair<string, string>> fields, string? decision = "allow")
            => string.Join('&', fields
                .Concat(decision is null ? Array.Empty<KeyValuePair<string, string>>() : new[] { new KeyValuePair<string, string>("decision", decision) })
                .Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}"));

        public static List<KeyValuePair<string, string>> With(this IEnumerable<KeyValuePair<string, string>> fields, string name, string value)
            => fields.Select(f => f.Key == name ? new KeyValuePair<string, string>(name, value) : f).ToList();
    }
}
