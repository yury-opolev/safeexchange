/// <summary>
/// CallerDenialHttpExtensionsTests
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Callers;
    using SafeExchange.Tests.Contract;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Uses the contract fixture plumbing (production serializer settings) so the bytes compare
    /// with what REST writes today.
    /// </summary>
    [TestFixture]
    public class CallerDenialHttpExtensionsTests : ContractTestBase
    {
        [Test]
        public async Task ToResponseAsync_Forbidden_IsByteIdenticalToActionResultsForbidden()
        {
            var expected = await ActionResults.ForbiddenAsync(Request("get"), "Not a member of a global group.", "consent_required");

            var actual = await CallerDenial.Forbidden("Not a member of a global group.", "consent_required").ToResponseAsync(Request("get"));

            Assert.That(actual.StatusCode, Is.EqualTo(expected.StatusCode));
            Assert.That(ContractAssert.HeaderLines(actual), Is.EqualTo(ContractAssert.HeaderLines(expected)));
            Assert.That(ContractAssert.ReadBodyBytes(actual), Is.EqualTo(ContractAssert.ReadBodyBytes(expected)));
        }

        [Test]
        public async Task ToResponseAsync_WritesStatusCodeAndAllFields()
        {
            var denial = new CallerDenial(HttpStatusCode.Unauthorized, "unauthorized", "Token missing.", string.Empty);

            var response = await denial.ToResponseAsync(Request("post"));

            ContractAssert.Json(response, HttpStatusCode.Unauthorized, """
                {"status":"unauthorized","subStatus":"","error":"Token missing."}
                """);
        }
    }
}
