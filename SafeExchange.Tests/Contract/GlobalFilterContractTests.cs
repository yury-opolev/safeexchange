/// <summary>
/// GlobalFilterContractTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using Microsoft.Azure.Functions.Worker.Http;
    using NUnit.Framework;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Model.Dto.Input;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Security.Claims;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Pins the global access filter (AllowedGroups) and the registered-application gate on every
    /// entry point of the six handlers. Each case arranges its data before the filter is
    /// configured, so only the filter outcome varies between scenarios.
    /// </summary>
    [TestFixture]
    public class GlobalFilterContractTests : ContractTestBase
    {
        private const string AllowedGroup = "00000000-0000-0000-0000-0000000000d1";

        private const string SecondAllowedGroup = "00000000-0000-0000-0000-0000000000d3";

        private const string OtherGroup = "00000000-0000-0000-0000-0000000000d2";

        private const string NotMemberBody = """
            {"status":"forbidden","subStatus":"","error":"Not a member of a global group."}
            """;

        private const string NotRegisteredBody = """
            {"status":"forbidden","subStatus":"","error":"Application is not registered or disabled."}
            """;

        private static readonly byte[] Hello = Encoding.UTF8.GetBytes("hello");

        public static IEnumerable<TestCaseData> EntryPoints()
        {
            yield return Case("SecretMeta.Run", (f, c, a) => f.SecretMeta.Run(Request("get"), "alpha", c, f.Logger));
            yield return Case("SecretMeta.RunList", (f, c, a) => f.SecretMeta.RunList(Request("get"), c, f.Logger));
            yield return Case("SecretMeta.RunListV3", (f, c, a) => f.SecretMeta.RunListV3(Request("get"), c, f.Logger));
            yield return Case("ContentMeta.Run", (f, c, a) => f.ContentMeta.Run(
                JsonRequest("post", new ContentMetadataCreationInput { ContentType = "application/pdf", FileName = "report.pdf" }),
                "alpha", string.Empty, c, f.Logger));
            yield return Case("ContentMeta.RunDrop", (f, c, a) => f.ContentMeta.RunDrop(Request("patch"), "alpha", a.Blank, c, f.Logger));
            yield return Case("SecretStream.Run (upload)", (f, c, a) => f.UploadChunkAsync("alpha", a.Blank, Hello, caller: c));
            yield return Case("SecretStream.Run (download)", (f, c, a) => f.SecretStream.Run(
                Request("get"), "alpha", a.Ready, $"{a.Ready}-00000000", c, f.Logger));
            yield return Case("SecretStream.RunContentDownload", (f, c, a) => f.SecretStream.RunContentDownload(
                Request("get"), "alpha", a.Ready, c, f.Logger));
            yield return Case("ContentCommit.Run", (f, c, a) =>
            {
                var request = TextRequest("patch", $"{{\"hash\":\"{Sha256Hex(Hello)}\"}}");
                request.Headers.Add(SafeExchangeSecretStream.AccessTicketHeaderName, a.HashedTicket);
                return f.ContentCommit.Run(request, "alpha", a.Hashed, c, f.Logger);
            });
            yield return Case("Access.Run", (f, c, a) => f.SecretAccess.Run(Request("get"), "alpha", c, f.Logger));
            yield return Case("SecretAudit.Run", (f, c, a) => f.SecretAudit.Run(Request("get"), "alpha", c, f.Logger));
        }

        [TestCaseSource(nameof(EntryPoints))]
        public async Task GroupConfigured_UserNotInUsers_Forbidden(Func<GlobalFilterContractTests, ClaimsPrincipal, Arrangement, Task<HttpResponseData>> call)
        {
            var arrangement = await this.ArrangeAsync();
            this.GlobalAccessGroups.AllowedGroups = AllowedGroup;

            var response = await call(this, this.Owner, arrangement);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, NotMemberBody);
        }

        [TestCaseSource(nameof(EntryPoints))]
        public async Task GroupConfigured_UserInUsersWithoutAllowedGroup_Forbidden(Func<GlobalFilterContractTests, ClaimsPrincipal, Arrangement, Task<HttpResponseData>> call)
        {
            var arrangement = await this.ArrangeAsync();
            this.GlobalAccessGroups.AllowedGroups = AllowedGroup;
            await this.SeedUserAsync(OwnerUpn, "00000000-0000-0000-0000-0000000000a1", false, OtherGroup);

            var response = await call(this, this.Owner, arrangement);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, NotMemberBody);
        }

        [TestCaseSource(nameof(EntryPoints))]
        public async Task GroupConfigured_UserInUsersWithoutAnyGroup_Forbidden(Func<GlobalFilterContractTests, ClaimsPrincipal, Arrangement, Task<HttpResponseData>> call)
        {
            var arrangement = await this.ArrangeAsync();
            this.GlobalAccessGroups.AllowedGroups = AllowedGroup;
            await this.SeedUserAsync(OwnerUpn, "00000000-0000-0000-0000-0000000000a1");

            var response = await call(this, this.Owner, arrangement);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, NotMemberBody);
        }

        [TestCaseSource(nameof(EntryPoints))]
        public async Task GroupConfigured_UserInAllowedGroup_NotForbidden(Func<GlobalFilterContractTests, ClaimsPrincipal, Arrangement, Task<HttpResponseData>> call)
        {
            var arrangement = await this.ArrangeAsync();
            this.GlobalAccessGroups.AllowedGroups = $"{AllowedGroup},{SecondAllowedGroup}";
            await this.SeedUserAsync(OwnerUpn, "00000000-0000-0000-0000-0000000000a1", false, OtherGroup, SecondAllowedGroup);

            var response = await call(this, this.Owner, arrangement);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [TestCaseSource(nameof(EntryPoints))]
        public async Task GroupConfigured_RegisteredApplication_Forbidden(Func<GlobalFilterContractTests, ClaimsPrincipal, Arrangement, Task<HttpResponseData>> call)
        {
            var arrangement = await this.ArrangeAsync();
            await this.SeedApplicationAsync(AgentClientId, AgentDisplayName, enabled: true);
            this.GlobalAccessGroups.AllowedGroups = AllowedGroup;

            var response = await call(this, this.Agent, arrangement);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, NotMemberBody);
        }

        [TestCaseSource(nameof(EntryPoints))]
        public async Task NoGroupConfigured_UnregisteredApplication_Forbidden(Func<GlobalFilterContractTests, ClaimsPrincipal, Arrangement, Task<HttpResponseData>> call)
        {
            var arrangement = await this.ArrangeAsync();

            var response = await call(this, this.UnregisteredApp, arrangement);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, NotRegisteredBody);
        }

        private static TestCaseData Case(string name, Func<GlobalFilterContractTests, ClaimsPrincipal, Arrangement, Task<HttpResponseData>> call)
            => new TestCaseData(call).SetName($"{{m}}({name})");

        private async Task<Arrangement> ArrangeAsync()
        {
            await this.CreateSecretAsync("alpha", auditEnabled: true);

            var ready = await this.CreateAttachmentAsync("alpha");
            Assert.That((await this.UploadChunkAsync("alpha", ready, Hello)).StatusCode, Is.EqualTo(HttpStatusCode.OK), "Arrange: ready upload failed.");

            var blank = await this.CreateAttachmentAsync("alpha");

            var hashed = await this.CreateAttachmentAsync("alpha");
            var hashedUpload = await this.UploadChunkAsync("alpha", hashed, Hello, chunkHash: Sha256Hex(Hello));
            Assert.That(hashedUpload.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Arrange: hashed upload failed.");

            return new Arrangement(ready, blank, hashed, TicketOf(hashedUpload));
        }

        public sealed record Arrangement(string Ready, string Blank, string Hashed, string HashedTicket);
    }
}
