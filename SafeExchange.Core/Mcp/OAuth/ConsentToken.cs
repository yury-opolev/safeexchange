/// <summary>
/// ConsentToken
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Buffers.Text;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;

    /// <summary>
    /// Binds the consent form to the browser that loaded it: the page carries
    /// <c>consent_token = base64url(HMAC-SHA256(key, "saex-mcp-consent\n" + nonce + "\n" + canonical fields))</c>, the
    /// nonce travels only in the <see cref="ConsentCookie"/>, and the canonical fields are the form fields in order as
    /// <c>name=value</c> pairs (both URI-escaped) joined by <c>&amp;</c>. Verification is fixed-time.
    /// </summary>
    public static class ConsentToken
    {
        public const int NonceLength = 43;

        private const string Purpose = "saex-mcp-consent\n";

        public static string NewNonce() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        public static bool IsNonce([NotNullWhen(true)] string? value)
            => value is { Length: NonceLength } && value.All(IsBase64UrlChar);

        public static string Create(byte[] key, string nonce, IEnumerable<KeyValuePair<string, string>> fields)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(nonce);
            ArgumentNullException.ThrowIfNull(fields);

            var canonical = string.Join('&', fields.Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}"));
            return Base64Url.EncodeToString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Purpose + nonce + "\n" + canonical)));
        }

        public static bool Verify(byte[] key, string? nonce, IEnumerable<KeyValuePair<string, string>> fields, string? token)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(fields);
            if (!IsNonce(nonce) || token is null || !token.All(IsBase64UrlChar))
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Create(key, nonce, fields)), Encoding.ASCII.GetBytes(token));
        }

        private static bool IsBase64UrlChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_';
    }
}
