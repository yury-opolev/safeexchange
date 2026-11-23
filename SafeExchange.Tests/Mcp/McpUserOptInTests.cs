/// <summary>
/// McpUserOptInTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using NUnit.Framework;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Services;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;

    /// <summary>
    /// A user may use MCP only after opting in (<c>McpAllowed</c>), and an administrator block
    /// (<c>McpBlockedByAdmin</c>) wins. Applications and REST are not affected.
    /// </summary>
    [TestFixture]
    public class McpUserOptInTests : McpHttpTestBase
    {
        private const string UserTelemetryId = "0123456789abcdef0123456789abcdef";

        private static readonly string WhoAmI = Rpc("tools/call", new { name = "whoami", arguments = new { } });

        [Test]
        public async Task UserWithoutAUsersRow_IsRefused_McpNotAllowed()
        {
            var response = await this.CallAsync(UserPrincipal(OwnerUpn));

            AssertDenied(response, McpUserAccessPolicy.NotAllowedSubStatus, McpUserAccessPolicy.NotAllowedError);
            Assert.That(this.Logs.Entries.Any(e => e.Contains("MCP tool")), Is.False);
        }

        [Test]
        public async Task UserWhoDidNotOptIn_IsRefused_McpNotAllowed()
        {
            await this.SeedMcpUserAsync(OwnerUpn, mcpAllowed: false);

            var response = await this.CallAsync(UserPrincipal(OwnerUpn));

            AssertDenied(response, McpUserAccessPolicy.NotAllowedSubStatus, McpUserAccessPolicy.NotAllowedError);
        }

        [Test]
        public async Task UserWhoOptedIn_CallsTools()
        {
            await this.SeedMcpUserAsync(OwnerUpn, mcpAllowed: true);

            var response = await this.CallAsync(UserPrincipal(OwnerUpn));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var data = JsonNode.Parse(BodyOf(response))!["result"]!["structuredContent"]!;
            Assert.That(data["subjectType"]!.GetValue<string>(), Is.EqualTo("User"));
            Assert.That(data["subjectId"]!.GetValue<string>(), Is.EqualTo(OwnerUpn));
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task BlockedUser_IsRefused_McpBlockedByAdmin_WhateverTheirChoice(bool mcpAllowed)
        {
            await this.SeedMcpUserAsync(OwnerUpn, mcpAllowed, mcpBlockedByAdmin: true);

            var response = await this.CallAsync(UserPrincipal(OwnerUpn));

            AssertDenied(response, McpUserAccessPolicy.BlockedByAdminSubStatus, McpUserAccessPolicy.BlockedByAdminError);
        }

        [Test]
        public async Task ChangedSetting_AppliesToTheNextMessage()
        {
            var user = await this.SeedMcpUserAsync(OwnerUpn, mcpAllowed: true);
            var allowed = await this.CallAsync(UserPrincipal(OwnerUpn));

            user.McpBlockedByAdmin = true;
            await this.DbContext.SaveChangesAsync();
            var refused = await this.CallAsync(UserPrincipal(OwnerUpn));

            Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            AssertDenied(refused, McpUserAccessPolicy.BlockedByAdminSubStatus, McpUserAccessPolicy.BlockedByAdminError);
        }

        [Test]
        public async Task Application_IsNotSubjectToTheUserSetting()
        {
            await this.SeedApplicationAsync(AgentClientId, "mcp-agent", enabled: true);

            var response = await this.CallAsync(AppPrincipal(AgentClientId));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task Rest_IsNotSubjectToTheUserSetting()
        {
            await this.SeedMcpUserAsync(OwnerUpn, mcpAllowed: false, mcpBlockedByAdmin: true);
            var secretMeta = new SafeExchangeSecretMeta(this.Services.GetRequiredService<ICallerResolver>(), this.Services.GetRequiredService<SecretService>());

            var response = await secretMeta.RunListV3(HttpRequest("get"), UserPrincipal(OwnerUpn), this.Logger());

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task Refusal_IsLoggedWithTheTelemetryId_NotTheUpn()
        {
            var user = await this.SeedMcpUserAsync(OwnerUpn, mcpAllowed: false);
            user.TelemetryId = UserTelemetryId;
            await this.DbContext.SaveChangesAsync();

            await this.CallAsync(UserPrincipal(OwnerUpn));

            Assert.That(this.Logs.Entries, Has.Some.Contains($"MCP refused for user (tid {UserTelemetryId}): mcp_not_allowed."));
            Assert.That(this.Logs.Entries, Has.None.Contains(OwnerUpn));
        }

        private static void AssertDenied(HttpResponseData response, string subStatus, string error)
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            var body = JsonNode.Parse(BodyOf(response))!.AsObject();
            Assert.That(body["status"]!.GetValue<string>(), Is.EqualTo("forbidden"));
            Assert.That(body["subStatus"]!.GetValue<string>(), Is.EqualTo(subStatus));
            Assert.That(body["error"]!.GetValue<string>(), Is.EqualTo(error));
        }

        private Task<HttpResponseData> CallAsync(ClaimsPrincipal principal)
            => this.Handler.Run(HttpRequest("post", WhoAmI), principal, this.Logger());

        private ILogger Logger() => this.Logs.CreateLogger("SafeExchange.Functions.SafeMcp");
    }
}
