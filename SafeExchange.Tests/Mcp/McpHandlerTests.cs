/// <summary>
/// McpHandlerTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using NUnit.Framework;
    using SafeExchange.Core.Functions.Mcp;
    using SafeExchange.Core.Model;
    using System.Linq;
    using System.Net;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;

    /// <summary>
    /// The Functions adapter: feature flag, method check, caller resolution before dispatch, and the session id
    /// carrying the client label into audit events.
    /// </summary>
    [TestFixture]
    public class McpHandlerTests : McpHttpTestBase
    {
        private const string InitializeBody = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"copilot-cli","version":"1.0.114"}}}
            """;

        [SetUp]
        public Task OptInOwnerAsync() => this.SeedMcpUserAsync(OwnerUpn, mcpAllowed: true);

        [Test]
        public async Task FlagOff_AnswersNotFound()
        {
            this.BuildProvider(("Features:McpEndpoint", "False"));

            var response = await this.Handler.Run(HttpRequest("post", InitializeBody), UserPrincipal(OwnerUpn), this.Logger());

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(BodyOf(response), Is.Empty);
        }

        [TestCase("get")]
        [TestCase("delete")]
        public async Task OtherMethods_AnswerMethodNotAllowed(string method)
        {
            var response = await this.Handler.Run(HttpRequest(method), UserPrincipal(OwnerUpn), this.Logger());

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
            Assert.That(response.Headers.GetValues("Allow"), Is.EqualTo(new[] { "POST" }));
        }

        [Test]
        public async Task DisabledApplication_IsDeniedLikeRest_BeforeDispatch()
        {
            await this.SeedApplicationAsync(AgentClientId, "mcp-agent", enabled: false);

            var response = await this.Handler.Run(
                HttpRequest("post", Rpc("tools/call", new { name = "whoami", arguments = new { } })), AppPrincipal(AgentClientId), this.Logger());

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(BodyOf(response), Is.EqualTo("""{"status":"forbidden","subStatus":"","error":"Application is not registered or disabled."}"""));
            Assert.That(this.Logs.Entries.Any(e => e.Contains("MCP tool")), Is.False);
        }

        [Test]
        public async Task EnabledApplication_CallsToolsAsItself()
        {
            await this.SeedApplicationAsync(AgentClientId, "mcp-agent", enabled: true);

            var response = await this.Handler.Run(
                HttpRequest("post", Rpc("tools/call", new { name = "whoami", arguments = new { } })), AppPrincipal(AgentClientId), this.Logger());

            var data = JsonNode.Parse(BodyOf(response))!["result"]!["structuredContent"]!;
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.GetValues("Content-Type"), Is.EqualTo(new[] { "application/json" }));
            Assert.That(data["subjectType"]!.GetValue<string>(), Is.EqualTo("Application"));
            Assert.That(data["subjectId"]!.GetValue<string>(), Is.EqualTo("mcp-agent"));
            Assert.That(data["untrusted"]!["clientLabel"], Is.Null);
        }

        [Test]
        public async Task SessionIdFromInitialize_LabelsTheAuditEventsOfLaterCalls()
        {
            var initialized = await this.Handler.Run(HttpRequest("post", InitializeBody), UserPrincipal(OwnerUpn), this.Logger());
            var sessionId = initialized.Headers.GetValues("Mcp-Session-Id").Single();
            var created = await this.Handler.Run(
                HttpRequest("post", Rpc("tools/call", new { name = "secret_create", arguments = new { name = "alpha", expirationSettings = Expiration(), auditEnabled = true } }), sessionId),
                UserPrincipal(OwnerUpn),
                this.Logger());

            var events = await this.DbContext.SecretAuditEvents.AsNoTracking().ToListAsync();
            Assert.That(sessionId, Is.EqualTo("Y29waWxvdC1jbGkvMS4wLjExNA"));
            Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(events.Select(e => e.EventType), Is.EqualTo(new[] { SecretAuditEventType.SecretCreated }));
            Assert.That(events[0].Channel, Is.EqualTo("mcp"));
            Assert.That(events[0].ClientLabel, Is.EqualTo("copilot-cli/1.0.114"));
            Assert.That(events[0].ActorSubjectId, Is.EqualTo(OwnerUpn));
        }

        [Test]
        public async Task MalformedSessionId_IsIgnored()
        {
            var created = await this.Handler.Run(
                HttpRequest("post", Rpc("tools/call", new { name = "secret_create", arguments = new { name = "alpha", expirationSettings = Expiration(), auditEnabled = true } }), "%%%"),
                UserPrincipal(OwnerUpn),
                this.Logger());

            var createdEvent = await this.DbContext.SecretAuditEvents.AsNoTracking().SingleAsync();
            Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(createdEvent.Channel, Is.EqualTo("mcp"));
            Assert.That(createdEvent.ClientLabel, Is.Empty);
        }

        [Test]
        public async Task Notification_IsAcceptedWithoutABody()
        {
            var response = await this.Handler.Run(
                HttpRequest("post", """{"jsonrpc":"2.0","method":"notifications/initialized"}"""), UserPrincipal(OwnerUpn), this.Logger());

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            Assert.That(BodyOf(response), Is.Empty);
            Assert.That(response.Headers.Contains("Content-Type"), Is.False);
        }

        [Test]
        public void ApiBaseUri_IsTheRestApiRootOfTheRequestHost()
        {
            Assert.That(McpHandler.ApiBaseUri(McpUrl).AbsoluteUri, Is.EqualTo("https://safeexchange.test/api/"));
        }

        private Microsoft.Extensions.Logging.ILogger Logger()
            => this.Logs.CreateLogger("SafeExchange.Functions.SafeMcp");
    }
}
