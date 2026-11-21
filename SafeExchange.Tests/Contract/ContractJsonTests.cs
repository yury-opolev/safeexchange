/// <summary>
/// ContractJsonTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using NUnit.Framework;
    using System.Collections.Generic;

    [TestFixture]
    public class ContractJsonTests
    {
        private static readonly IReadOnlyDictionary<string, string> NoLiterals = new Dictionary<string, string>();

        [Test]
        public void Canonical_KeepsPropertyOrder_RemovesWhitespace_UnescapesQuotes()
        {
            var canonical = ContractJson.Canonical("{ \"b\": 1,\n \"a\": \"it\\u0027s\" }");

            Assert.That(canonical, Is.EqualTo("{\"b\":1,\"a\":\"it's\"}"));
        }

        [Test]
        public void IsWireCompact_AcceptsDefaultEncoderOutput_RejectsIndentedOrRelaxed()
        {
            Assert.That(ContractJson.IsWireCompact("{\"a\":\"it\\u0027s\",\"b\":[1,2]}"), Is.True);
            Assert.That(ContractJson.IsWireCompact("{ \"a\": 1 }"), Is.False);
            Assert.That(ContractJson.IsWireCompact("{\"a\":\"it's\"}"), Is.False);
        }

        [Test]
        public void Normalize_MasksNonEmptyNamedValues_AtAnyDepth_KeepsEmptyStrings()
        {
            var json = "{\"result\":{\"accessTicket\":\"abc-123\",\"items\":[{\"accessTicket\":\"\"},{\"hash\":\"x\"}]}}";

            var normalized = ContractJson.Normalize(json, new[] { "accessTicket", "hash" }, NoLiterals);

            Assert.That(
                normalized,
                Is.EqualTo("{\"result\":{\"accessTicket\":\"<accessTicket>\",\"items\":[{\"accessTicket\":\"\"},{\"hash\":\"<hash>\"}]}}"));
        }

        [Test]
        public void Normalize_ReplacesLiteralsAndCorrelationId()
        {
            var json = "{\"name\":\"BLOB-1-00000000\",\"error\":\"Internal error. Reference: 0123456789abcdef0123456789abcdef\"}";
            var literals = new Dictionary<string, string> { { "BLOB-1", "<main>" } };

            var normalized = ContractJson.Normalize(json, new string[0], literals);

            Assert.That(
                normalized,
                Is.EqualTo("{\"name\":\"<main>-00000000\",\"error\":\"Internal error. Reference: <correlationId>\"}"));
        }
    }
}
