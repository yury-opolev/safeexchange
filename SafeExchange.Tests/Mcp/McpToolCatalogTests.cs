/// <summary>
/// McpToolCatalogTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using NUnit.Framework;
    using System.Linq;
    using System.Net;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;

    /// <summary>
    /// tools/list from the production registrations (<c>AddSafeExchangeMcp</c>).
    /// </summary>
    [TestFixture]
    public class McpToolCatalogTests : McpTestBase
    {
        private static readonly string[] ToolNames =
        {
            "whoami", "secret_list", "secret_get", "secret_create", "secret_update", "secret_delete", "secret_read_text",
            "file_upload_begin", "file_upload_chunk", "file_upload_complete", "file_download_chunk", "file_delete",
            "file_transfer_info", "access_list", "access_change", "audit_read",
        };

        private static readonly string[] Destructive = { "secret_delete", "file_delete", "access_change" };

        private static readonly string[] ReadOnly =
        {
            "whoami", "secret_list", "secret_get", "secret_read_text", "file_download_chunk", "file_transfer_info", "access_list", "audit_read",
        };

        [Test]
        public async Task ToolsList_ListsTheSixteenToolsWithObjectSchemasAndHints()
        {
            var response = await this.RpcAsync(Rpc("tools/list"));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var tools = JsonNode.Parse(response.Body!)!["result"]!["tools"]!.AsArray().Select(t => t!.AsObject()).ToList();
            Assert.That(tools.Select(NameOf), Is.EqualTo(ToolNames));
            foreach (var tool in tools)
            {
                var name = NameOf(tool);
                Assert.That(tool["description"]!.GetValue<string>(), Is.Not.Empty, name);
                Assert.That(tool["inputSchema"]!["type"]!.GetValue<string>(), Is.EqualTo("object"), name);
                Assert.That(tool["annotations"]!["openWorldHint"]!.GetValue<bool>(), Is.False, name);
                Assert.That(tool["annotations"]!["readOnlyHint"]!.GetValue<bool>(), Is.EqualTo(ReadOnly.Contains(name)), name);
                Assert.That(tool["annotations"]!["destructiveHint"]?.GetValue<bool>() ?? false, Is.EqualTo(Destructive.Contains(name)), name);
            }

            Assert.That(tools.Single(t => NameOf(t) == "file_upload_chunk")["inputSchema"]!["required"]!.ToJsonString(),
                Is.EqualTo("""["name","contentId","index","data","sha256"]"""));
            Assert.That(tools.Single(t => NameOf(t) == "secret_create")["inputSchema"]!["properties"]!["expirationSettings"]!["properties"]!["idleTimeToExpire"], Is.Not.Null);
        }

        private static string NameOf(JsonObject tool) => tool["name"]!.GetValue<string>();
    }
}
