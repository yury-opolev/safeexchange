/// <summary>
/// AccessContractTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using NUnit.Framework;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Pins the wire contract of SafeExchangeAccess (post/get/delete/patch). GET differs by
    /// route version: v2 passes enrichedAccessList=false, v3 passes true; the other methods
    /// are version-agnostic.
    /// </summary>
    [TestFixture]
    public class AccessContractTests : ContractTestBase
    {
        [Test]
        public async Task Grant_ReturnsOk()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretAccess.Run(JsonRequest("post", ReaderInput(read: true)), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":"ok"}
                """);
        }

        [Test]
        public async Task GetV2_ReturnsPlainAccessList()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.SecretAccess.Run(Request("get"), "alpha", this.Owner, this.Logger, enrichedAccessList: false);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":[
                 {"objectName":"alpha","subjectType":0,"subjectName":"owner@contract.test","subjectId":"owner@contract.test","canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true,"tags":[]},
                 {"objectName":"alpha","subjectType":0,"subjectName":"reader@contract.test","subjectId":"reader@contract.test","canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false,"tags":[]}]}
                """);
        }

        [Test]
        public async Task GetV3_ReturnsAccessListWithCallerEffectivePermissions()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.SecretAccess.Run(Request("get"), "alpha", this.Reader, this.Logger, enrichedAccessList: true);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"accessList":[
                 {"objectName":"alpha","subjectType":0,"subjectName":"owner@contract.test","subjectId":"owner@contract.test","canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true,"tags":[]},
                 {"objectName":"alpha","subjectType":0,"subjectName":"reader@contract.test","subjectId":"reader@contract.test","canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false,"tags":[]}],
                 "callerEffectivePermissions":{"canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false}}}
                """);
        }

        [Test]
        public async Task Revoke_ReturnsOk()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.SecretAccess.Run(JsonRequest("delete", ReaderInput(read: true)), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":"ok"}
                """);
        }

        [Test]
        public async Task Patch_ReturnsOk()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true);
            var input = new AccessUpdateInput
            {
                Add = ReaderInput(read: true, write: true),
                Remove = ReaderInput(read: true),
            };

            var response = await this.SecretAccess.Run(JsonRequest("patch", input), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":"ok"}
                """);
        }

        [Test]
        public async Task Patch_WithoutAddOrRemove_ErrorStatus()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretAccess.Run(TextRequest("patch", "{}"), "alpha", this.Owner, this.Logger);

            // Quirk: SafeExchangeAccess reports 400s with status "error", not "bad_request".
            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"error","subStatus":"","error":"Access update body must include at least one of 'add' or 'remove'."}
                """);
        }

        [Test]
        public async Task Grant_EmptyList_ErrorStatus()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretAccess.Run(TextRequest("post", "[]"), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"error","subStatus":"","error":"Access settings are not provided."}
                """);
        }

        [Test]
        public async Task MissingSecret_NotFoundWithoutTrailingPeriod()
        {
            var response = await this.SecretAccess.Run(Request("get"), "missing", this.Owner, this.Logger, enrichedAccessList: true);

            // Quirk: "not exists" wording and no trailing period, unlike the other handlers.
            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Secret 'missing' not exists"}
                """);
        }

        [Test]
        public async Task Get_WithoutReadPermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretAccess.Run(Request("get"), "alpha", this.Stranger, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'Read' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task Get_CallerRequiringConsent_ForbiddenWithConsentSubStatus()
        {
            await this.CreateSecretAsync("alpha");
            this.DbContext.Users.Add(new User("Stranger", "00000000-0000-0000-0000-0000000000a3", TenantId, StrangerUpn, StrangerUpn)
            {
                ConsentRequired = true,
            });
            await this.DbContext.SaveChangesAsync();

            var response = await this.SecretAccess.Run(Request("get"), "alpha", this.Stranger, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"consent_required","error":"Insufficient permissions to do 'Read' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task Grant_WithoutGrantAccessPermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.SecretAccess.Run(JsonRequest("post", ReaderInput(read: true, write: true)), "alpha", this.Reader, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'GrantAccess' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task Patch_RemoveWithoutRevokeAccessPermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true, grant: true);
            var input = new AccessUpdateInput { Remove = ReaderInput(read: true) };

            var response = await this.SecretAccess.Run(JsonRequest("patch", input), "alpha", this.Reader, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'RevokeAccess' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task Revoke_WithoutRevokeAccessPermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true, grant: true);

            var response = await this.SecretAccess.Run(JsonRequest("delete", ReaderInput(read: true)), "alpha", this.Reader, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'RevokeAccess' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task Grant_CallerRequiringConsent_ForbiddenWithConsentSubStatus()
        {
            await this.CreateSecretAsync("alpha");
            await this.SeedUserAsync(StrangerUpn, "00000000-0000-0000-0000-0000000000a3", consentRequired: true);

            var response = await this.SecretAccess.Run(JsonRequest("post", ReaderInput(read: true)), "alpha", this.Stranger, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"consent_required","error":"Insufficient permissions to do 'GrantAccess' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task Revoke_CallerRequiringConsent_ForbiddenWithConsentSubStatus()
        {
            await this.CreateSecretAsync("alpha");
            await this.SeedUserAsync(StrangerUpn, "00000000-0000-0000-0000-0000000000a3", consentRequired: true);

            var response = await this.SecretAccess.Run(JsonRequest("delete", ReaderInput(read: true)), "alpha", this.Stranger, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"consent_required","error":"Insufficient permissions to do 'RevokeAccess' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task UnknownMethod_ErrorStatus()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretAccess.Run(Request("put"), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"error","subStatus":"","error":"Request method not recognized"}
                """);
        }

        [Test]
        public async Task RegisteredApplication_GetV3_OnGrantedSecret()
        {
            await this.SeedApplicationAsync(AgentClientId, AgentDisplayName, enabled: true);
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", AgentDisplayName, read: true, subjectType: SubjectTypeInput.Application);

            var response = await this.SecretAccess.Run(Request("get"), "alpha", this.Agent, this.Logger, enrichedAccessList: true);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"accessList":[
                 {"objectName":"alpha","subjectType":0,"subjectName":"owner@contract.test","subjectId":"owner@contract.test","canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true,"tags":[]},
                 {"objectName":"alpha","subjectType":100,"subjectName":"contract-agent","subjectId":"contract-agent","canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false,"tags":[]}],
                 "callerEffectivePermissions":{"canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false}}}
                """);
        }

        private static List<SubjectPermissionsInput> ReaderInput(bool read, bool write = false)
            => new()
            {
                new SubjectPermissionsInput
                {
                    SubjectType = SubjectTypeInput.User,
                    SubjectName = ReaderUpn,
                    CanRead = read,
                    CanWrite = write,
                },
            };
    }
}
