/// <summary>
/// McpSecretWriteToolsTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using Microsoft.EntityFrameworkCore;
    using NUnit.Framework;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Permissions;
    using System.Linq;
    using System.Threading.Tasks;

    /// <summary>
    /// secret_create, secret_update, secret_delete and secret_read_text over the real services: one success and
    /// one denial per tool, the confirm/reveal guards, and the text value round trip.
    /// </summary>
    [TestFixture]
    public class McpSecretWriteToolsTests : McpTestBase
    {
        private const string SecretText = "p&ss<w>\"'\nsecond line";

        [Test]
        public async Task SecretCreate_CreatesAnOwnedSecret_WithTagsUntrusted()
        {
            var result = await this.CallToolAsync(this.McpOwner, "secret_create", new
            {
                name = "alpha",
                expirationSettings = Expiration(),
                tags = new[] { "Team:A" },
                auditEnabled = true,
            });

            var data = Ok(result);
            Assert.That(data["name"]!.GetValue<string>(), Is.EqualTo("alpha"));
            Assert.That(data["auditEnabled"]!.GetValue<bool>(), Is.True);
            Assert.That(data["expirationSettings"]!["idleTimeToExpire"]!.GetValue<string>(), Is.EqualTo("180.00:00:00"));
            Assert.That(data["content"]!.AsArray().Single()!["isMain"]!.GetValue<bool>(), Is.True);
            Assert.That(data["untrusted"]!["tags"]!.ToJsonString(), Is.EqualTo("""["team:a"]"""));
            Assert.That(TextOf(result), Does.Contain("<untrusted>{\"tags\":[\"team:a\"],\"files\":[]}</untrusted>"));
            Assert.That(await this.PermissionsManager.IsAuthorizedAsync(SubjectType.User, OwnerUpn, "alpha", PermissionType.Full), Is.True);
        }

        [Test]
        public async Task SecretCreate_RestValidation_IsReportedAsToolErrors()
        {
            AssertToolError(
                await this.CallToolAsync(this.McpOwner, "secret_create", new { name = "alpha" }),
                "bad_request", "Expiration settings are not provided.");
            Ok(await this.CallToolAsync(this.McpOwner, "secret_create", new { name = "alpha", expirationSettings = Expiration() }));
            AssertToolError(
                await this.CallToolAsync(this.McpOwner, "secret_create", new { name = "alpha", expirationSettings = Expiration() }),
                "conflict", "Secret 'alpha' already exists.");

            var malformed = await this.CallToolAsync(this.McpOwner, "secret_create", new { name = "beta", expirationSettings = new { expireAt = "soon" } });
            Assert.That(malformed["structuredContent"]!["status"]!.GetValue<string>(), Is.EqualTo("invalid_arguments"));
            Assert.That(malformed["structuredContent"]!["error"]!.GetValue<string>(), Does.StartWith("The arguments must match the input schema:"));
        }

        [Test]
        public async Task SecretCreate_WithText_StoresItAsMainContent_AndSecretReadTextReturnsIt()
        {
            var created = Ok(await this.CallToolAsync(this.McpOwner, "secret_create", new { name = "alpha", expirationSettings = Expiration(), text = SecretText }));
            var read = Ok(await this.CallToolAsync(this.McpOwner, "secret_read_text", new { name = "alpha", reveal = true }));

            Assert.That(created["content"]![0]!["isReady"]!.GetValue<bool>(), Is.True);
            Assert.That(read["hasText"]!.GetValue<bool>(), Is.True);
            Assert.That(read["untrusted"]!["text"]!.GetValue<string>(), Is.EqualTo(SecretText));
            Assert.That(read["untrusted"]!["html"]!.GetValue<string>(), Is.EqualTo("<p>p&amp;ss&lt;w&gt;\"'</p><p>second line</p>"));
        }

        [Test]
        public async Task SecretUpdate_ReplacesSettingsAndTags_AndNeedsWrite()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);

            var updated = Ok(await this.CallToolAsync(this.McpOwner, "secret_update", new { name = "alpha", expirationSettings = Expiration(), tags = new[] { "x" } }));
            var reader = await this.CallToolAsync(this.McpReader, "secret_update", new { name = "alpha", expirationSettings = Expiration() });

            Assert.That(updated["untrusted"]!["tags"]!.ToJsonString(), Is.EqualTo("""["x"]"""));
            AssertToolError(reader, "forbidden", "Insufficient permissions to do 'Write' action on 'alpha'");
        }

        [Test]
        public async Task SecretDelete_NeedsConfirmAndWrite()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);

            var unconfirmed = await this.CallToolAsync(this.McpOwner, "secret_delete", new { name = "alpha" });
            var reader = await this.CallToolAsync(this.McpReader, "secret_delete", new { name = "alpha", confirm = true });
            Assert.That(await this.DbContext.Objects.CountAsync(o => o.ObjectName == "alpha"), Is.EqualTo(1));
            var deleted = Ok(await this.CallToolAsync(this.McpOwner, "secret_delete", new { name = "alpha", confirm = true }));

            AssertToolError(unconfirmed, "confirmation_required", "Deleting secret 'alpha' removes it, its text and its files for everyone. Call again with confirm=true to delete it.");
            AssertToolError(reader, "forbidden", "Insufficient permissions to do 'Write' action on 'alpha'");
            Assert.That(deleted.ToJsonString(), Is.EqualTo("""{"name":"alpha","deleted":true}"""));
            Assert.That(await this.DbContext.Objects.CountAsync(o => o.ObjectName == "alpha"), Is.EqualTo(0));
        }

        [Test]
        public async Task SecretReadText_NeedsRevealAndRead_AndAuditsTheReadAsMcp()
        {
            Ok(await this.CallToolAsync(this.McpOwner, "secret_create", new { name = "alpha", expirationSettings = Expiration(), auditEnabled = true, text = SecretText }));

            var unrevealed = await this.CallToolAsync(this.McpOwner, "secret_read_text", new { name = "alpha" });
            var readsBefore = await this.DbContext.SecretAuditEvents.AsNoTracking().CountAsync(e => e.EventType == SecretAuditEventType.ContentRead);
            var stranger = await this.CallToolAsync(this.McpStranger, "secret_read_text", new { name = "alpha", reveal = true });
            Ok(await this.CallToolAsync(this.McpOwner, "secret_read_text", new { name = "alpha", reveal = true }));
            var reads = await this.DbContext.SecretAuditEvents.AsNoTracking().Where(e => e.EventType == SecretAuditEventType.ContentRead).ToListAsync();

            AssertToolError(unrevealed, "reveal_required", "Reading secret 'alpha' puts its value into this conversation and sends it to the model provider. Call again with reveal=true to read it.");
            AssertToolError(stranger, "forbidden", "Insufficient permissions to do 'Read' action on 'alpha'");
            Assert.That(readsBefore, Is.EqualTo(0));
            Assert.That(reads, Has.Count.EqualTo(1));
            Assert.That(reads[0].Channel, Is.EqualTo("mcp"));
            Assert.That(reads[0].ClientLabel, Is.EqualTo(Label));
        }

        [Test]
        public async Task SecretReadText_WithoutText_SaysSo()
        {
            await this.CreateSecretAsync("alpha");

            var data = Ok(await this.CallToolAsync(this.McpOwner, "secret_read_text", new { name = "alpha", reveal = true }));

            Assert.That(data.ToJsonString(), Is.EqualTo("""{"name":"alpha","hasText":false}"""));
        }
    }
}
