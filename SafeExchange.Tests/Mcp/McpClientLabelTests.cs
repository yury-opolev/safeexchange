/// <summary>
/// McpClientLabelTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using ModelContextProtocol.Protocol;
    using NUnit.Framework;
    using SafeExchange.Core.Mcp;
    using System;
    using System.Buffers.Text;
    using System.Text;

    [TestFixture]
    public class McpClientLabelTests
    {
        [Test]
        public void ToSessionId_IsBase64UrlOfNameSlashVersion_AndDecodesBack()
        {
            var sessionId = McpClientLabel.ToSessionId(new Implementation { Name = "copilot-cli", Version = "1.0.114" });

            Assert.That(sessionId, Is.EqualTo("Y29waWxvdC1jbGkvMS4wLjExNA"));
            Assert.That(McpClientLabel.FromSessionId(sessionId), Is.EqualTo("copilot-cli/1.0.114"));
        }

        [Test]
        public void ToSessionId_SanitisesTheLabel_AndIsNullWhenNothingIsLeft()
        {
            var sessionId = McpClientLabel.ToSessionId(new Implementation { Name = "evil\nclient\u00e9", Version = "2" });

            Assert.That(McpClientLabel.FromSessionId(sessionId), Is.EqualTo("evilclient/2"));
            Assert.That(McpClientLabel.ToSessionId(new Implementation { Name = "\u00e9", Version = "" }), Is.Null);
            Assert.That(McpClientLabel.ToSessionId(null), Is.Null);
        }

        [Test]
        public void FromSessionId_SanitisesAndTruncatesWhatTheClientSends()
        {
            var crafted = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("x\u0001y/" + new string('v', 140)));

            var label = McpClientLabel.FromSessionId(crafted);

            Assert.That(label, Does.StartWith("xy/vvv"));
            Assert.That(label, Has.Length.EqualTo(128));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not base64url!")]
        [TestCase("a+b/c=")]
        [TestCase("__4")]
        [TestCase("AAAA")]
        public void FromSessionId_MissingOrMalformed_IsNull(string? sessionId)
        {
            Assert.That(McpClientLabel.FromSessionId(sessionId), Is.Null);
        }

        [Test]
        public void FromSessionId_TooLong_IsNull()
        {
            Assert.That(McpClientLabel.FromSessionId(new string('A', 300)), Is.Null);
        }
    }
}
