/// <summary>
/// BrokerState
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Buffers.Text;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Nodes;

    /// <summary>
    /// The <c>state</c> the broker sends to Entra: <c>base64url(JSON{ r: clientRedirect, s: clientState })</c>, at
    /// most <see cref="MaxEncodedLength"/> characters. It carries no authority (the redirect is loopback-only and
    /// re-validated on decode), so it is neither signed nor encrypted.
    /// </summary>
    public sealed record BrokerState(string RedirectUri, string? ClientState)
    {
        public const int MaxEncodedLength = 2048;

        private const string RedirectName = "r";

        private const string ClientStateName = "s";

        public bool TryEncode([NotNullWhen(true)] out string? encoded)
        {
            var payload = new JsonObject { [RedirectName] = this.RedirectUri };
            if (this.ClientState is not null)
            {
                payload[ClientStateName] = this.ClientState;
            }

            var value = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload));
            encoded = value.Length <= MaxEncodedLength ? value : null;
            return encoded is not null;
        }

        public static bool TryDecode(string? encoded, [NotNullWhen(true)] out BrokerState? state)
        {
            state = null;
            if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxEncodedLength || !encoded.All(IsBase64UrlChar))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(encoded));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                string? redirectUri = null;
                string? clientState = null;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    if (property.Name == RedirectName && redirectUri is null)
                    {
                        redirectUri = property.Value.GetString();
                    }
                    else if (property.Name == ClientStateName && clientState is null)
                    {
                        clientState = property.Value.GetString();
                    }
                    else
                    {
                        return false;
                    }
                }

                if (!LoopbackRedirectUri.IsValid(redirectUri))
                {
                    return false;
                }

                state = new BrokerState(redirectUri, clientState);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return false;
            }
        }

        private static bool IsBase64UrlChar(char c)
            => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_';
    }
}
