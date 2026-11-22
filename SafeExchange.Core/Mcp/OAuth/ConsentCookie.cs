/// <summary>
/// ConsentCookie
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;

    /// <summary>
    /// The <c>__Host-saex-mcp-consent</c> cookie that carries the consent nonce from the consent page to the consent
    /// form post and the callback: <c>Secure; HttpOnly; SameSite=Lax; Path=/</c> (Lax so that Entra's top-level
    /// redirect to the callback still sends it), valid for <see cref="Lifetime"/>.
    /// </summary>
    public sealed record ConsentCookie(string Value, int MaxAgeSeconds)
    {
        public const string Name = "__Host-saex-mcp-consent";

        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

        public static ConsentCookie Expire { get; } = new(string.Empty, 0);

        public static ConsentCookie Issue(string nonce)
        {
            ArgumentNullException.ThrowIfNull(nonce);
            return new ConsentCookie(nonce, (int)Lifetime.TotalSeconds);
        }

        public string ToSetCookieHeader() => $"{Name}={this.Value}; Max-Age={this.MaxAgeSeconds}; Path=/; Secure; HttpOnly; SameSite=Lax";

        /// <summary>
        /// The value of the first occurrence of the cookie in a <c>Cookie</c> request header (exact, case-sensitive
        /// name); null when it is absent or empty.
        /// </summary>
        public static string? Read(string? cookieHeader)
        {
            foreach (var pair in (cookieHeader ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (pair.StartsWith(Name + "=", StringComparison.Ordinal))
                {
                    var value = pair[(Name.Length + 1)..];
                    return value.Length > 0 ? value : null;
                }
            }

            return null;
        }
    }
}
