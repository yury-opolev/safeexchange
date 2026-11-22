/// <summary>
/// ClientAddressTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;

    [TestFixture]
    public class ClientAddressTests
    {
        [TestCase("203.0.113.7", "203.0.113.7")]
        [TestCase("203.0.113.7:50123", "203.0.113.7")]
        [TestCase("198.51.100.1, 203.0.113.7:50123", "203.0.113.7")]
        [TestCase("spoofed-by-the-client,203.0.113.7:50123", "203.0.113.7")]
        [TestCase("[2001:db8::1]:50123", "2001:db8::1")]
        [TestCase("2001:DB8:0:0:0:0:0:1", "2001:db8::1")]
        [TestCase("198.51.100.1, [2001:db8::1]:443", "2001:db8::1")]
        public void TheLastForwardedForEntry_IsTheClientAddress_WithoutItsPort(string header, string expected)
        {
            Assert.That(ClientAddress.FromForwardedFor(header), Is.EqualTo(expected));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" , ")]
        [TestCase("unknown")]
        [TestCase("203.0.113.7, not-an-address")]
        [TestCase("[2001:db8::1")]
        public void AMissingOrUnparsableLastEntry_IsNoAddress(string? header)
        {
            Assert.That(ClientAddress.FromForwardedFor(header), Is.Null);
        }
    }
}
