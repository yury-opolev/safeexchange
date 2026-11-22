/// <summary>
/// McpOAuthBrokerTestBase
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Text.Json.Nodes;

    /// <summary>
    /// A broker over plain options (MCP on, client ID and Entra authority set), a <see cref="FakeEntraTokenClient"/>
    /// and every log entry captured in <see cref="Logs"/>.
    /// </summary>
    public abstract class McpOAuthBrokerTestBase
    {
        protected const string BrokerClientId = "00000000-0000-0000-0000-0000000000c9";

        protected const string Tenant = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001";

        protected const string ApiScope = "api://safeexchange.test/user_impersonation";

        protected const string ClientRedirect = "http://127.0.0.1:53682/callback";

        protected const string ClientState = "rmcp-csrf-token-123";

        protected const string Callback = "https://safeexchange.test/api/oauth/callback";

        // RFC 7636 appendix B.
        protected const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

        protected const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        protected static readonly Uri ApiBase = new("https://safeexchange.test/api/");

        protected McpOAuthBroker Broker { get; private set; } = null!;

        protected FakeEntraTokenClient Entra { get; private set; } = null!;

        protected RecordingLoggerProvider Logs { get; private set; } = null!;

        [SetUp]
        public void BrokerSetUp()
        {
            this.Entra = new FakeEntraTokenClient();
            this.Logs = new RecordingLoggerProvider();
            this.Broker = this.CreateBroker();
        }

        protected McpOAuthBroker CreateBroker(bool mcpEnabled = true, string clientId = BrokerClientId, string authority = Tenant, string stateKey = McpOAuthTestKey.Value, EntraCallGate? gate = null)
        {
            var authentication = Mock.Of<IOptionsMonitor<AuthenticationConfiguration>>(x => x.CurrentValue == new AuthenticationConfiguration
            {
                Authority = authority,
                ValidAudiences = "00000000-0000-0000-0000-0000000000e1,api://safeexchange.test",
            });
            var oauth = Mock.Of<IOptionsMonitor<McpOAuthOptions>>(x => x.CurrentValue == new McpOAuthOptions { ClientId = clientId, StateKey = stateKey });
            var features = Mock.Of<IOptionsMonitor<Features>>(x => x.CurrentValue == new Features { McpEndpoint = mcpEnabled });
            var loggerFactory = new LoggerFactory(new[] { this.Logs });
            return new McpOAuthBroker(
                new McpProtectedResource(authentication, oauth),
                oauth,
                authentication,
                features,
                this.Entra,
                gate ?? new EntraCallGate(16, EntraCallGate.DefaultWait),
                loggerFactory.CreateLogger<McpOAuthBroker>());
        }

        protected static JsonObject Json(string? body) => JsonNode.Parse(body!)!.AsObject();

        protected static string Query(params (string Name, string Value)[] parameters)
            => string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value)}"));

        protected static string ValidQuery(params (string Name, string Value)[] extra)
            => Query(new[]
            {
                ("client_id", BrokerClientId),
                ("redirect_uri", ClientRedirect),
                ("response_type", "code"),
                ("code_challenge", Challenge),
                ("code_challenge_method", "S256"),
                ("state", ClientState),
            }.Concat(extra).ToArray());

        /// <summary>
        /// Opens the consent page for <paramref name="query"/>: its hidden form fields and the consent cookie's nonce.
        /// </summary>
        protected (List<KeyValuePair<string, string>> Fields, string Nonce) OpenConsent(string query)
        {
            var page = this.Broker.Authorize(ApiBase, query);
            Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Arrange: the consent page.");
            return (ConsentForm.HiddenFields(page.Body), page.Cookie!.Value);
        }

        /// <summary>
        /// Opens the consent page and allows it: the signed state Entra receives and the consent cookie's nonce.
        /// </summary>
        protected (string State, string Nonce) AllowConsent(string query)
        {
            var (fields, nonce) = this.OpenConsent(query);
            var (_, parameters) = SplitLocation(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields), nonce).Location);
            return (parameters["state"], nonce);
        }

        protected static void AssertInvalidPage(McpOAuthResult result)
        {
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(result.Location, Is.Null);
            Assert.That(result.ContentType, Is.EqualTo("text/html; charset=utf-8"));
            Assert.That(result.Body, Does.Contain(ConsentPage.InvalidRequestMessage));
        }

        /// <summary>
        /// Splits a redirect URL into the part before <c>?</c> and its decoded parameters.
        /// </summary>
        protected static (string Target, IReadOnlyDictionary<string, string> Parameters) SplitLocation(string? location)
        {
            Assert.That(location, Is.Not.Null);
            var separator = location!.IndexOf('?');
            Assert.That(OAuthParameters.TryParse(location[(separator + 1)..], out var parameters), Is.True);
            return (location[..separator], parameters!);
        }
    }
}
