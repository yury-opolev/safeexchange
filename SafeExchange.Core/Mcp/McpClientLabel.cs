/// <summary>
/// McpClientLabel
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using ModelContextProtocol.Protocol;
    using SafeExchange.Core.Audit;
    using System;
    using System.Buffers.Text;
    using System.Text;

    /// <summary>
    /// Carries the MCP client's self-reported label (<c>clientInfo.name/clientInfo.version</c>) in the
    /// <c>Mcp-Session-Id</c> header as base64url. The endpoint keeps no session state: the header only lets later
    /// stateless requests say which client they come from. The label carries no authority.
    /// </summary>
    public static class McpClientLabel
    {
        public const string SessionIdHeaderName = "Mcp-Session-Id";

        // Base64url of a 128-character label is 171 characters; anything much longer is not ours.
        private const int MaxSessionIdLength = 256;

        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>
        /// The session id to return from <c>initialize</c>, or <c>null</c> when the client sent nothing usable.
        /// </summary>
        public static string? ToSessionId(Implementation? clientInfo)
        {
            if (clientInfo is null)
            {
                return null;
            }

            var label = AuditOrigin.Sanitize($"{clientInfo.Name}/{clientInfo.Version}");
            return label.Length == 0 || label == "/"
                ? null
                : Base64Url.EncodeToString(Encoding.UTF8.GetBytes(label));
        }

        /// <summary>
        /// The sanitised label from a session id header value; <c>null</c> when it is missing or malformed.
        /// </summary>
        public static string? FromSessionId(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || sessionId.Length > MaxSessionIdLength || !Base64Url.IsValid(sessionId))
            {
                return null;
            }

            try
            {
                var label = AuditOrigin.Sanitize(StrictUtf8.GetString(Base64Url.DecodeFromChars(sessionId)));
                return label.Length == 0 ? null : label;
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                return null;
            }
        }
    }
}
