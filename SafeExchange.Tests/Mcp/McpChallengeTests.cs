/// <summary>
/// McpChallengeTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Middleware;
    using SafeExchange.Tests.Contract;
    using SafeExchange.Tests.Utilities;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// The 401 written by the authentication middlewares (RFC 9728 challenge only while MCP is enabled, body
    /// unchanged) and the predicate that leaves only the metadata trigger anonymous.
    /// </summary>
    [TestFixture]
    public class McpChallengeTests : McpHttpTestBase
    {
        [Test]
        public async Task Unauthorized_WithMcpEnabled_ChallengesWithTheMetadataUrl_AndKeepsTheBody()
        {
            var expected = await LegacyUnauthorizedAsync("Bearer token is not present or invalid.");

            var response = await UnauthorizedResponse.CreateAsync(HttpRequest("post"), "Bearer token is not present or invalid.", includeResourceMetadata: true);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Headers.GetValues("WWW-Authenticate"), Is.EqualTo(new[]
            {
                "Bearer resource_metadata=\"https://safeexchange.test/api/.well-known/oauth-protected-resource\"",
            }));
            Assert.That(BodyOf(response), Is.EqualTo(BodyOf(expected)));
            Assert.That(BodyOf(response), Is.EqualTo("""{"status":"unauthorized","subStatus":"","error":"Bearer token is not present or invalid."}"""));
        }

        [Test]
        public async Task Unauthorized_WithMcpDisabled_IsTheUnchangedResponse()
        {
            var expected = await LegacyUnauthorizedAsync(DefaultAuthenticationMiddleware.InvalidTokenErrorMessage);

            var response = await UnauthorizedResponse.CreateAsync(HttpRequest("get"), DefaultAuthenticationMiddleware.InvalidTokenErrorMessage, includeResourceMetadata: false);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(ContractAssert.HeaderLines(response), Is.EqualTo(ContractAssert.HeaderLines(expected)));
            Assert.That(BodyOf(response), Is.EqualTo(BodyOf(expected)));
        }

        [TestCase("SafeExchange-OAuthProtectedResource", "httpTrigger", false)]
        [TestCase("SafeExchange-Mcp", "httpTrigger", true)]
        [TestCase("SafeExchange-SecretMeta", "httpTrigger", true)]
        [TestCase("SafeExchange-Purge", "timerTrigger", false)]
        public void OnlyTheMetadataTrigger_SkipsAuthentication(string functionName, string triggerType, bool authenticated)
        {
            var context = new TestFunctionContext(Definition(functionName, triggerType), new Mock<FunctionInvocation>().Object);

            Assert.That(SafeExchangeFunctionsStartup.IsAuthenticatedHttpTrigger(context), Is.EqualTo(authenticated));
        }

        // The 401 exactly as both middlewares wrote it before the MCP endpoint existed.
        private static async Task<HttpResponseData> LegacyUnauthorizedAsync(string errorMessage)
        {
            var response = HttpRequest("post").CreateResponse();
            response.StatusCode = HttpStatusCode.Unauthorized;
            await response.WriteAsJsonAsync(new BaseResponseObject<object> { Status = "unauthorized", Error = errorMessage });
            return response;
        }

        private static FunctionDefinition Definition(string functionName, string triggerType)
        {
            var binding = new Mock<BindingMetadata>();
            binding.SetupGet(b => b.Type).Returns(triggerType);
            binding.SetupGet(b => b.Name).Returns("request");
            var definition = new Mock<FunctionDefinition>();
            definition.SetupGet(d => d.Name).Returns(functionName);
            definition.SetupGet(d => d.InputBindings).Returns(ImmutableDictionary.CreateRange(new[]
            {
                new KeyValuePair<string, BindingMetadata>("request", binding.Object),
            }));
            return definition.Object;
        }
    }
}
