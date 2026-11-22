/// <summary>
/// OAuthParametersTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Collections.Generic;

    [TestFixture]
    public class OAuthParametersTests
    {
        [TestCase("a=1&b=two+words&c=%2Fpath%3Fx%3D1")]
        [TestCase("?a=1&b=two%20words&c=/path?x=1")]
        public void Parse_DecodesAQueryOrForm(string encoded)
        {
            Assert.That(OAuthParameters.TryParse(encoded, out var parameters), Is.True);

            Assert.That(parameters, Is.EqualTo(new Dictionary<string, string>
            {
                ["a"] = "1",
                ["b"] = "two words",
                ["c"] = "/path?x=1",
            }));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("?")]
        [TestCase("a=&b&&")]
        public void ParametersWithoutAValue_AreTreatedAsOmitted(string? encoded)
        {
            Assert.That(OAuthParameters.TryParse(encoded, out var parameters), Is.True);

            Assert.That(parameters, Is.Empty);
        }

        [TestCase("a=1&a=2")]
        [TestCase("a=1&b=2&a=1")]
        [TestCase("a=1&%61=2")]
        public void ARepeatedParameter_IsRejected(string encoded)
        {
            Assert.That(OAuthParameters.TryParse(encoded, out var parameters), Is.False);

            Assert.That(parameters, Is.Null);
        }
    }
}
