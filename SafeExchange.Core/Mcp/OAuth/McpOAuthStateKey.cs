/// <summary>
/// McpOAuthStateKey
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Diagnostics.CodeAnalysis;

    /// <summary>
    /// Decodes <c>Mcp:OAuth:StateKey</c>: standard (padded) base64 of at least <see cref="MinLength"/> bytes. The ARM
    /// default <c>[base64(concat(newGuid(), newGuid()))]</c> decodes to 72 bytes.
    /// </summary>
    public static class McpOAuthStateKey
    {
        public const int MinLength = 32;

        public static bool TryDecode(string? value, [NotNullWhen(true)] out byte[]? key)
        {
            key = null;
            var text = (value ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return false;
            }

            var buffer = new byte[text.Length];
            if (!Convert.TryFromBase64String(text, buffer, out var written) || written < MinLength)
            {
                return false;
            }

            key = buffer[..written];
            return true;
        }
    }
}
