/// <summary>
/// AuthorizationServerMetadataTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;

    [TestFixture]
    public class AuthorizationServerMetadataTests
    {
        [Test]
        public void TheDocument_DescribesThisHostAsAPublicClientPkceAuthorizationServer()
        {
            var json = AuthorizationServerMetadata.ToJson(new Uri("https://safeexchange.test/api/"), new[] { "api://safeexchange.test/user_impersonation", "offline_access" });

            Assert.That(json, Is.EqualTo(
                "{\"issuer\":\"https://safeexchange.test/api\","
                + "\"authorization_endpoint\":\"https://safeexchange.test/api/oauth/authorize\","
                + "\"token_endpoint\":\"https://safeexchange.test/api/oauth/token\","
                + "\"registration_endpoint\":\"https://safeexchange.test/api/oauth/register\","
                + "\"response_types_supported\":[\"code\"],"
                + "\"grant_types_supported\":[\"authorization_code\",\"refresh_token\"],"
                + "\"code_challenge_methods_supported\":[\"S256\"],"
                + "\"token_endpoint_auth_methods_supported\":[\"none\"],"
                + "\"scopes_supported\":[\"api://safeexchange.test/user_impersonation\",\"offline_access\"]}"));
        }

        [Test]
        public void TheIssuer_HasNoTrailingSlash_AndEndpointsKeepTheHostAndPort()
        {
            var apiBase = new Uri("https://saex.example:8443/api/");

            Assert.That(McpOAuthPaths.Issuer(apiBase), Is.EqualTo("https://saex.example:8443/api"));
            Assert.That(McpOAuthPaths.Endpoint(apiBase, McpOAuthPaths.Callback), Is.EqualTo("https://saex.example:8443/api/oauth/callback"));
            Assert.That(McpOAuthPaths.Endpoint(apiBase, McpOAuthPaths.Mcp), Is.EqualTo("https://saex.example:8443/api/mcp"));
        }
    }
}
