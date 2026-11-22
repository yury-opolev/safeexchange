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
        [TestCase("[2001:db8:1:2::1]:50123", "2001:db8:1:2::/64")]
        [TestCase("2001:DB8:1:2:0:0:0:1", "2001:db8:1:2::/64")]
        [TestCase("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
        [TestCase("198.51.100.1, [2001:db8:1:2:aaaa:bbbb:cccc:dddd]:443", "2001:db8:1:2::/64")]
        [TestCase("[2001:db8::1]:50123", "2001:db8:0:0::/64")]
        [TestCase("::1", "0:0:0:0::/64")]
        [TestCase("::ffff:203.0.113.7", "203.0.113.7")]
        [TestCase("[::ffff:203.0.113.7]:50123", "203.0.113.7")]
        [TestCase("[::FFFF:CB00:7107]:50123", "203.0.113.7")]
        public void TheLastForwardedForEntry_IsTheClientAddress_WithoutItsPort_AndIPv6IsItsSlash64Prefix(string header, string expected)
        {
            Assert.That(ClientAddress.FromForwardedFor(header), Is.EqualTo(expected));
        }

        [Test]
        public void TwoHostsOfOneIPv6Slash64_ShareAKey_AndOtherPrefixesDoNot()
        {
            var first = ClientAddress.FromForwardedFor("[2001:db8:1:2::1]:1");
            var second = ClientAddress.FromForwardedFor("[2001:db8:1:2:ffff:ffff:ffff:ffff]:2");
            var other = ClientAddress.FromForwardedFor("[2001:db8:1:3::1]:1");

            Assert.That(second, Is.EqualTo(first));
            Assert.That(other, Is.Not.EqualTo(first));
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
