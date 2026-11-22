/// <summary>
/// SignedBrokerState
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Buffers.Text;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;

    /// <summary>
    /// The <c>state</c> the broker sends to Entra: <c>base64url(payload) + "." + base64url(HMAC-SHA256)</c> with
    /// payload <c>{"r": clientRedirect, "s": clientState|null, "n": consentNonce, "e": unixExpiry}</c>, valid for
    /// <see cref="Lifetime"/> and at most <see cref="MaxEncodedLength"/> characters. The HMAC (key
    /// <c>Mcp:OAuth:StateKey</c>) covers <c>"saex-mcp-state\n" + payload segment</c> and is checked in fixed time before
    /// the payload is parsed; the redirect is re-validated as loopback after that.
    /// </summary>
    public sealed record SignedBrokerState(string RedirectUri, string? ClientState, string Nonce, long ExpiresAt)
    {
        public const int MaxEncodedLength = 2048;

        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

        private const string Purpose = "saex-mcp-state\n";

        private const int SignatureLength = 32;

        private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };

        public static bool TryCreate(byte[] key, string redirectUri, string? clientState, string nonce, DateTime utcNow, [NotNullWhen(true)] out string? encoded)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(redirectUri);
            ArgumentNullException.ThrowIfNull(nonce);

            var payload = new JsonObject
            {
                ["r"] = redirectUri,
                ["s"] = clientState,
                ["n"] = nonce,
                ["e"] = UnixSeconds(utcNow + Lifetime),
            };
            var segment = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload));
            var value = segment + "." + Base64Url.EncodeToString(Sign(key, segment));
            encoded = value.Length <= MaxEncodedLength ? value : null;
            return encoded is not null;
        }

        public static bool TryVerify(byte[] key, string? encoded, DateTime utcNow, [NotNullWhen(true)] out SignedBrokerState? state)
        {
            ArgumentNullException.ThrowIfNull(key);
            state = null;
            if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxEncodedLength)
            {
                return false;
            }

            var parts = encoded.Split('.');
            if (parts.Length != 2 || parts[0].Length == 0 || !parts.All(IsBase64Url))
            {
                return false;
            }

            try
            {
                var signature = Base64Url.DecodeFromChars(parts[1]);
                if (signature.Length != SignatureLength || !CryptographicOperations.FixedTimeEquals(signature, Sign(key, parts[0])))
                {
                    return false;
                }

                if (!TryRead(Base64Url.DecodeFromChars(parts[0]), out var decoded)
                    || decoded.ExpiresAt <= UnixSeconds(utcNow)
                    || !LoopbackRedirectUri.IsValid(decoded.RedirectUri))
                {
                    return false;
                }

                state = decoded;
                return true;
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
            {
                return false;
            }
        }

        private static byte[] Sign(byte[] key, string segment)
            => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Purpose + segment));

        // Exactly r (string), s (string or null), n (non-empty string) and e (integer), each once.
        private static bool TryRead(byte[] json, [NotNullWhen(true)] out SignedBrokerState? state)
        {
            state = null;
            using var document = JsonDocument.Parse(json, StrictJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Count() != 4)
            {
                return false;
            }

            var root = document.RootElement;
            if (!root.TryGetProperty("r", out var redirect) || redirect.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("s", out var clientState) || clientState.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                || !root.TryGetProperty("n", out var nonce) || nonce.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("e", out var expiry) || expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt64(out var expiresAt))
            {
                return false;
            }

            var nonceValue = nonce.GetString()!;
            if (nonceValue.Length == 0)
            {
                return false;
            }

            state = new SignedBrokerState(redirect.GetString()!, clientState.GetString(), nonceValue, expiresAt);
            return true;
        }

        /// <summary>
        /// Seconds since the Unix epoch; a <see cref="DateTimeKind.Unspecified"/> value counts as UTC.
        /// </summary>
        public static long UnixSeconds(DateTime utcNow)
            => new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds();

        private static bool IsBase64Url(string value)
            => value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
    }
}
