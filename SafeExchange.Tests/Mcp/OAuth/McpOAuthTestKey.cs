/// <summary>
/// McpOAuthTestKey
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using System;

    /// <summary>
    /// The fixed broker state key of the tests (<c>Mcp:OAuth:StateKey</c>): base64 of the 32 bytes 0x01 to 0x20.
    /// </summary>
    public static class McpOAuthTestKey
    {
        public const string Value = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";

        public static byte[] Bytes => Convert.FromBase64String(Value);
    }
}
