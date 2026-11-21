/// <summary>
/// AuditOriginTests
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Model;
    using System;

    [TestFixture]
    public class AuditOriginTests
    {
        [Test]
        public void FromCaller_RestCaller_IsNone()
        {
            var origin = AuditOrigin.FromCaller(MakeCaller(CallerChannels.Rest, "ignored/1.0"));

            Assert.That(origin, Is.EqualTo(AuditOrigin.None));
            Assert.That(origin.Channel, Is.Empty);
            Assert.That(origin.ClientLabel, Is.Empty);
            Assert.That(origin.IsEmpty, Is.True);
        }

        [Test]
        public void FromCaller_McpCaller_CarriesChannelAndLabel()
        {
            var origin = AuditOrigin.FromCaller(MakeCaller(CallerChannels.Mcp, "copilot-cli/1.0.114"));

            Assert.That(origin.Channel, Is.EqualTo("mcp"));
            Assert.That(origin.ClientLabel, Is.EqualTo("copilot-cli/1.0.114"));
            Assert.That(origin.IsEmpty, Is.False);
        }

        [Test]
        public void FromCaller_McpCallerWithoutLabel_HasAnEmptyLabel()
        {
            var origin = AuditOrigin.FromCaller(MakeCaller(CallerChannels.Mcp, null));

            Assert.That(origin.Channel, Is.EqualTo("mcp"));
            Assert.That(origin.ClientLabel, Is.Empty);
        }

        [Test]
        public void Create_StripsNonPrintableAndNonAsciiCharacters()
        {
            var origin = AuditOrigin.Create("mcp", "  agent\u0000/1.0\r\n\u00e9\u200b-x\t ");

            Assert.That(origin.ClientLabel, Is.EqualTo("agent/1.0-x"));
        }

        [Test]
        public void Create_TruncatesTheLabelTo128Characters()
        {
            var origin = AuditOrigin.Create("mcp", new string('a', 200));

            Assert.That(origin.ClientLabel, Has.Length.EqualTo(AuditOrigin.MaxLength));
            Assert.That(AuditOrigin.MaxLength, Is.EqualTo(128));
        }

        [Test]
        public void Create_DropsThePipeSoTheHashSuffixStaysUnambiguous()
        {
            var origin = AuditOrigin.Create("m|cp", "agent|1|v2");

            Assert.That(origin.Channel, Is.EqualTo("mcp"));
            Assert.That(origin.ClientLabel, Is.EqualTo("agent1v2"));
        }

        [Test]
        public void Create_NothingLeft_IsNone()
        {
            Assert.That(AuditOrigin.Create(null, null), Is.EqualTo(AuditOrigin.None));
            Assert.That(AuditOrigin.Create(" ", "\u0001\u00e9"), Is.EqualTo(AuditOrigin.None));
        }

        private static Caller MakeCaller(string channel, string? clientLabel)
            => new(SubjectType.User, "u@x", "U", Array.Empty<string>(), channel, clientLabel);
    }
}
