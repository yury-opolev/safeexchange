/// <summary>
/// McpAccessAuditToolsTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using NUnit.Framework;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Permissions;
    using System.Linq;
    using System.Threading.Tasks;

    /// <summary>
    /// access_list, access_change and audit_read over the real services.
    /// </summary>
    [TestFixture]
    public class McpAccessAuditToolsTests : McpTestBase
    {
        [Test]
        public async Task AccessList_PutsTheSubjectsUnderUntrusted_AndNeedsRead()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);

            var owner = Ok(await this.CallToolAsync(this.McpOwner, "access_list", new { name = "alpha" }));
            var stranger = await this.CallToolAsync(this.McpStranger, "access_list", new { name = "alpha" });

            Assert.That(owner["count"]!.GetValue<int>(), Is.EqualTo(2));
            Assert.That(owner["callerEffectivePermissions"]!["canGrantAccess"]!.GetValue<bool>(), Is.True);
            var subjects = owner["untrusted"]!["accessList"]!.AsArray().Select(e => e!["subjectName"]!.GetValue<string>());
            Assert.That(subjects, Is.EquivalentTo(new[] { OwnerUpn, ReaderUpn }));
            AssertToolError(stranger, "forbidden", "Insufficient permissions to do 'Read' action on 'alpha'");
        }

        [Test]
        public async Task AccessChange_GrantsAndRevokes_WithTheRestRules()
        {
            await this.CreateSecretAsync("alpha");
            var readerCanRead = new[] { new { subjectType = 0, subjectName = ReaderUpn, canRead = true } };

            var granted = Ok(await this.CallToolAsync(this.McpOwner, "access_change", new { name = "alpha", grant = readerCanRead }));
            var readerCouldRead = await this.PermissionsManager.IsAuthorizedAsync(SubjectType.User, ReaderUpn, "alpha", PermissionType.Read);
            var byReader = await this.CallToolAsync(this.McpReader, "access_change", new { name = "alpha", grant = new[] { new { subjectName = StrangerUpn, canRead = true } } });
            var revoked = Ok(await this.CallToolAsync(this.McpOwner, "access_change", new { name = "alpha", revoke = readerCanRead }));
            var empty = await this.CallToolAsync(this.McpOwner, "access_change", new { name = "alpha" });
            var malformed = await this.CallToolAsync(this.McpOwner, "access_change", new { name = "alpha", grant = "everyone" });

            Assert.That(granted.ToJsonString(), Is.EqualTo("""{"name":"alpha","granted":1,"revoked":0}"""));
            Assert.That(readerCouldRead, Is.True);
            AssertToolError(byReader, "forbidden", "Insufficient permissions to do 'GrantAccess' action on 'alpha'");
            Assert.That(revoked["revoked"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(await this.PermissionsManager.IsAuthorizedAsync(SubjectType.User, ReaderUpn, "alpha", PermissionType.Read), Is.False);
            AssertToolError(empty, "invalid_arguments", "Give at least one entry in 'grant' or 'revoke'.");
            Assert.That(malformed["structuredContent"]!["error"]!.GetValue<string>(), Does.StartWith("'grant' must match the input schema:"));
        }

        [Test]
        public async Task AuditRead_ReturnsTheEventsUntrusted_WithTheMcpOrigin()
        {
            Ok(await this.CallToolAsync(this.McpOwner, "secret_create", new { name = "alpha", expirationSettings = Expiration(), auditEnabled = true }));
            await this.CreateSecretAsync("plain");

            var page = Ok(await this.CallToolAsync(this.McpOwner, "audit_read", new { name = "alpha", direction = "desc", pageSize = 10 }));
            var plain = Ok(await this.CallToolAsync(this.McpOwner, "audit_read", new { name = "plain" }));
            var stranger = await this.CallToolAsync(this.McpStranger, "audit_read", new { name = "alpha" });
            var badDirection = await this.CallToolAsync(this.McpOwner, "audit_read", new { name = "alpha", direction = "sideways" });
            var badFrom = await this.CallToolAsync(this.McpOwner, "audit_read", new { name = "alpha", from = "yesterday" });

            Assert.That(page["auditEnabled"]!.GetValue<bool>(), Is.True);
            Assert.That(page["count"]!.GetValue<int>(), Is.EqualTo(1));
            var created = page["untrusted"]!["events"]![0]!;
            Assert.That(created["eventType"]!.GetValue<string>(), Is.EqualTo("SecretCreated"));
            Assert.That(created["channel"]!.GetValue<string>(), Is.EqualTo("mcp"));
            Assert.That(created["clientLabel"]!.GetValue<string>(), Is.EqualTo(Label));
            Assert.That(plain.ToJsonString(), Is.EqualTo("""{"name":"plain","auditEnabled":false,"count":0}"""));
            AssertToolError(stranger, "forbidden", "Insufficient permissions to do 'Read' action on 'alpha'");
            AssertToolError(badDirection, "invalid_arguments", "'direction' must be 'asc' or 'desc'.");
            AssertToolError(badFrom, "invalid_arguments", "'from' must be an ISO 8601 date-time.");
        }
    }
}
