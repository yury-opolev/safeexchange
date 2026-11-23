/// <summary>
/// McpOAuthBrokerTokenTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;

    [TestFixture]
    public class McpOAuthBrokerTokenTests : McpOAuthBrokerTestBase
    {
        private const string Code = "1.AQEA-fake-entra-authorization-code";

        [Test]
        public async Task AuthorizationCode_IsForwardedWithTheBrokersClientCallbackAndScope_AndNothingElse()
        {
            var body = Query(
                ("grant_type", "authorization_code"),
                ("code", Code),
                ("code_verifier", Verifier),
                ("redirect_uri", ClientRedirect),
                ("client_id", BrokerClientId),
                ("resource", "https://safeexchange.test/api/v1/mcp"),
                ("client_secret", "should-never-reach-entra"),
                ("scope", "openid https://graph.microsoft.com/.default"));

            var result = await this.Broker.TokenAsync(ApiBase, body);

            Assert.That(this.Entra.Requests, Is.EqualTo(new[]
            {
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = BrokerClientId,
                    ["code"] = Code,
                    ["code_verifier"] = Verifier,
                    ["redirect_uri"] = Callback,
                    ["scope"] = ApiScope + " offline_access",
                },
            }));
            Assert.That(result, Is.EqualTo(McpOAuthResult.Json(HttpStatusCode.OK, FakeEntraTokenClient.SuccessBody)));
        }

        [Test]
        public async Task RefreshToken_IsForwardedWithTheBrokersClientAndScope_AndNothingElse()
        {
            var body = Query(("grant_type", "refresh_token"), ("refresh_token", FakeEntraTokenClient.RefreshToken), ("client_id", BrokerClientId), ("resource", "https://safeexchange.test/api/v1/mcp"), ("client_secret", "x"));

            var result = await this.Broker.TokenAsync(ApiBase, body);

            Assert.That(this.Entra.Requests, Is.EqualTo(new[]
            {
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = BrokerClientId,
                    ["refresh_token"] = FakeEntraTokenClient.RefreshToken,
                    ["scope"] = ApiScope + " offline_access",
                },
            }));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task AnEntraError_IsRelayedUnchanged()
        {
            const string EntraError = "{\"error\":\"invalid_grant\",\"error_description\":\"AADSTS70008: The provided authorization code or refresh token has expired.\",\"error_codes\":[70008]}";
            this.Entra.Response = new EntraTokenResponse(HttpStatusCode.BadRequest, EntraError);

            var result = await this.Broker.TokenAsync(ApiBase, Query(("grant_type", "refresh_token"), ("refresh_token", FakeEntraTokenClient.RefreshToken), ("client_id", BrokerClientId)));

            Assert.That(result, Is.EqualTo(McpOAuthResult.Json(HttpStatusCode.BadRequest, EntraError)));
        }

        [TestCase("grant_type=client_credentials&scope=x&client_id=" + BrokerClientId, "unsupported_grant_type")]
        [TestCase("grant_type=password&username=u&password=p&client_id=" + BrokerClientId, "unsupported_grant_type")]
        [TestCase("code=c&code_verifier=" + Verifier + "&client_id=" + BrokerClientId, "invalid_request")]
        [TestCase("grant_type=authorization_code&code_verifier=" + Verifier + "&client_id=" + BrokerClientId, "invalid_request")]
        [TestCase("grant_type=authorization_code&code=c&client_id=" + BrokerClientId, "invalid_request")]
        [TestCase("grant_type=authorization_code&code=c&code_verifier=too-short&client_id=" + BrokerClientId, "invalid_request")]
        [TestCase("grant_type=refresh_token&client_id=" + BrokerClientId, "invalid_request")]
        [TestCase("grant_type=refresh_token&refresh_token=a&refresh_token=b&client_id=" + BrokerClientId, "invalid_request")]
        [TestCase("grant_type=refresh_token&refresh_token=a&client_id=00000000-0000-0000-0000-0000000000ff", "invalid_client")]
        [TestCase("grant_type=refresh_token&refresh_token=a", "invalid_client")]
        [TestCase("grant_type=refresh_token&refresh_token=a&client_id=", "invalid_client")]
        [TestCase("", "invalid_request")]
        public async Task AnInvalidRequest_IsRefused_WithoutCallingEntra(string body, string error)
        {
            var result = await this.Broker.TokenAsync(ApiBase, body);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(Json(result.Body)["error"]!.GetValue<string>(), Is.EqualTo(error));
            Assert.That(this.Entra.Requests, Is.Empty);
        }

        [Test]
        public async Task ABodyOver16KB_IsRefused_WithoutCallingEntra()
        {
            var body = Query(("grant_type", "refresh_token"), ("refresh_token", new string('r', McpOAuthBroker.MaxBodyLength)), ("client_id", BrokerClientId));

            var result = await this.Broker.TokenAsync(ApiBase, body);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(this.Entra.Requests, Is.Empty);
        }

        [Test]
        public async Task AnUnreachableEntra_IsABadGateway()
        {
            this.Entra.Failure = new HttpRequestException("No such host is known. (login.microsoftonline.com:443)");

            var result = await this.Broker.TokenAsync(ApiBase, Query(("grant_type", "refresh_token"), ("refresh_token", FakeEntraTokenClient.RefreshToken), ("client_id", BrokerClientId)));

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(Json(result.Body)["error"]!.GetValue<string>(), Is.EqualTo("server_error"));
        }

        [Test]
        public async Task AnEntraTimeout_IsABadGateway()
        {
            this.Entra.Failure = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.");

            var result = await this.Broker.TokenAsync(ApiBase, Query(("grant_type", "refresh_token"), ("refresh_token", FakeEntraTokenClient.RefreshToken), ("client_id", BrokerClientId)));

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
        }
    }
}
