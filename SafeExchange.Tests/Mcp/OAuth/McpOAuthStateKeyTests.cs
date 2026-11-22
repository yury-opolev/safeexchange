/// <summary>
/// McpOAuthStateKeyTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Linq;
    using System.Text;

    [TestFixture]
    public class McpOAuthStateKeyTests
    {
        [Test]
        public void ABase64KeyOf32Bytes_IsDecoded()
        {
            Assert.That(McpOAuthStateKey.TryDecode(McpOAuthTestKey.Value, out var key), Is.True);
            Assert.That(key, Is.EqualTo(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));
        }

        // What the ARM default [base64(concat(newGuid(), newGuid()))] produces: 72 ASCII bytes.
        [Test]
        public void TheArmDefaultShape_IsAccepted()
        {
            var value = Convert.ToBase64String(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString() + Guid.NewGuid().ToString()));

            Assert.That(McpOAuthStateKey.TryDecode(value, out var key), Is.True);
            Assert.That(key!.Length, Is.EqualTo(72));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHw==")]
        [TestCase("not base64 at all, but long enough to be 32 characters")]
        [TestCase("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA")]
        public void AnythingElse_IsRejected(string? value)
        {
            Assert.That(McpOAuthStateKey.TryDecode(value, out var key), Is.False);
            Assert.That(key, Is.Null);
        }
    }
}
