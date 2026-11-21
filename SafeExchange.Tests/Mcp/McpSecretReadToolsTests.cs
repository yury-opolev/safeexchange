/// <summary>
/// McpSecretReadToolsTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using Microsoft.EntityFrameworkCore;
    using NUnit.Framework;
    using SafeExchange.Core.Permissions;
    using System.Linq;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;

    /// <summary>
    /// whoami, secret_list and secret_get over the real services.
    /// </summary>
    [TestFixture]
    public class McpSecretReadToolsTests : McpTestBase
    {
        [Test]
        public async Task Whoami_DescribesTheCaller_WithTheDisplayNameAndLabelUntrusted()
        {
            var data = Ok(await this.CallToolAsync(this.McpOwner, "whoami"));

            Assert.That(data.ToJsonString(), Is.EqualTo(new JsonObject
            {
                ["subjectType"] = "User",
                ["subjectId"] = OwnerUpn,
                ["channel"] = "mcp",
                ["untrusted"] = new JsonObject { ["displayName"] = OwnerUpn, ["clientLabel"] = Label },
            }.ToJsonString()));
        }

        [Test]
        public async Task SecretList_FiltersByName_AndListsOnlyReadableSecrets()
        {
            await this.CreateSecretAsync("alpha");
            await this.CreateSecretAsync("beta");
            var tagged = await this.DbContext.Objects.SingleAsync(o => o.ObjectName == "alpha");
            tagged.Tags = new() { "ops" };
            await this.DbContext.SaveChangesAsync();
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);

            var owner = Ok(await this.CallToolAsync(this.McpOwner, "secret_list", new { nameFilter = "ALP" }));
            var reader = Ok(await this.CallToolAsync(this.McpReader, "secret_list"));
            var stranger = Ok(await this.CallToolAsync(this.McpStranger, "secret_list"));

            Assert.That(owner["count"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(owner["secrets"]![0]!["objectName"]!.GetValue<string>(), Is.EqualTo("alpha"));
            Assert.That(owner["secrets"]![0]!["callerEffectivePermissions"]!["canRevokeAccess"]!.GetValue<bool>(), Is.True);
            Assert.That(owner["untrusted"]!["tags"]!.ToJsonString(), Is.EqualTo("""[{"objectName":"alpha","tags":["ops"]}]"""));
            Assert.That(reader["secrets"]!.AsArray().Select(s => s!["objectName"]!.GetValue<string>()), Is.EqualTo(new[] { "alpha" }));
            Assert.That(reader["secrets"]![0]!["canWrite"]!.GetValue<bool>(), Is.False);
            Assert.That(stranger.ToJsonString(), Is.EqualTo("""{"count":0,"secrets":[]}"""));
        }

        [Test]
        public async Task SecretGet_ReturnsTheMetadata_AndNeedsRead()
        {
            await this.CreateSecretAsync("alpha");

            var owner = Ok(await this.CallToolAsync(this.McpOwner, "secret_get", new { name = "alpha" }));
            var stranger = await this.CallToolAsync(this.McpStranger, "secret_get", new { name = "alpha" });
            var missing = await this.CallToolAsync(this.McpOwner, "secret_get", new { name = "nope" });

            Assert.That(owner["name"]!.GetValue<string>(), Is.EqualTo("alpha"));
            Assert.That(owner["content"]![0]!["isMain"]!.GetValue<bool>(), Is.True);
            Assert.That(owner["content"]![0]!["chunkCount"]!.GetValue<int>(), Is.EqualTo(0));
            Assert.That(owner["untrusted"]!.ToJsonString(), Is.EqualTo("""{"tags":[],"files":[]}"""));
            AssertToolError(stranger, "forbidden", "Insufficient permissions to do 'Read' action on 'alpha'");
            AssertToolError(missing, "not_found", "Secret 'nope' does not exist.");
        }

        [Test]
        public async Task SecretGet_NameOfTheWrongType_IsAnArgumentError()
        {
            var result = await this.CallToolAsync(this.McpOwner, "secret_get", new { name = 5 });

            AssertToolError(result, "invalid_arguments", "'name' must be a string.");
        }
    }
}
