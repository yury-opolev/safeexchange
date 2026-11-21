/// <summary>
/// McpFileTransferInfoTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using NUnit.Framework;
    using System.Linq;
    using System.Threading.Tasks;

    /// <summary>
    /// file_transfer_info: the REST chunk endpoints of this host, the scopes to request and an example, never a token.
    /// </summary>
    [TestFixture]
    public class McpFileTransferInfoTests : McpTestBase
    {
        [Test]
        public async Task Upload_DescribesTheRestEndpointsOfThisHost_WithoutAToken()
        {
            var result = await this.CallToolAsync(this.McpOwner, "file_transfer_info", new { name = "alpha", direction = "upload" });

            var data = Ok(result);
            var steps = data["steps"]!.AsArray();
            Assert.That(steps.Select(s => s!["url"]!.GetValue<string>()), Is.EqualTo(new[]
            {
                "https://safeexchange.test/api/v2/secret/alpha/content",
                "https://safeexchange.test/api/v2/secret/alpha/content/{contentId}/chunk",
                "https://safeexchange.test/api/v2/secret/alpha/content/{contentId}/commit",
            }));
            Assert.That(steps[1]!["headers"]!["X-SafeExchange-Chunk-Hash"], Is.Not.Null);
            Assert.That(steps[2]!["method"]!.GetValue<string>(), Is.EqualTo("PATCH"));
            Assert.That(data["scopes"]!["user"]!.GetValue<string>(), Is.EqualTo("api://safeexchange.test/user_impersonation"));
            Assert.That(data["scopes"]!["application"]!.GetValue<string>(), Is.EqualTo("api://safeexchange.test/.default"));
            Assert.That(data["example"]!.GetValue<string>(), Does.StartWith("TOKEN=$(az account get-access-token --scope \"api://safeexchange.test/user_impersonation\" --query accessToken -o tsv)"));
            Assert.That(TextOf(result), Does.Not.Contain("eyJ"));
            Assert.That(data["untrusted"], Is.Null);
        }

        [Test]
        public async Task Download_UsesTheEscapedContentId_AndTheConfiguredScopes()
        {
            this.BuildProvider(("Authentication:McpScopes", "api://custom/files.read, api://custom/files.write"));

            var data = Ok(await this.CallToolAsync(this.McpOwner, "file_transfer_info", new { name = "alpha", contentId = "BLOB-1 2", direction = "download" }));
            var invalid = await this.CallToolAsync(this.McpOwner, "file_transfer_info", new { name = "alpha", direction = "sideways" });

            Assert.That(data["steps"]![1]!["url"]!.GetValue<string>(), Is.EqualTo("https://safeexchange.test/api/v2/secret/alpha/content/BLOB-1%202/all"));
            Assert.That(data["scopes"]!["user"]!.GetValue<string>(), Is.EqualTo("api://custom/files.read"));
            AssertToolError(invalid, "invalid_arguments", "'direction' must be 'upload' or 'download'.");
        }
    }
}
