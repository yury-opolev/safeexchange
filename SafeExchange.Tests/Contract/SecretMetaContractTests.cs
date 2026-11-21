/// <summary>
/// SecretMetaContractTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using NUnit.Framework;
    using SafeExchange.Core.Model.Dto.Input;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Pins the wire contract of SafeExchangeSecretMeta: Run (post/get/patch/delete),
    /// RunList (v2 secret-list) and RunListV3 (v3 secret-list). Run is route-version agnostic.
    /// </summary>
    [TestFixture]
    public class SecretMetaContractTests : ContractTestBase
    {
        [Test]
        public async Task Create_ReturnsCreatedMetadata()
        {
            var response = await this.SecretMeta.Run(JsonRequest("post", CreationInput()), "alpha", this.Owner, this.Logger);

            var main = await this.MainContentNameAsync("alpha");
            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"objectName":"alpha",
                 "content":[{"contentName":"<main>","isMain":true,"isImage":false,"contentType":"","fileName":"","isReady":false,"chunks":[]}],
                 "expirationSettings":{"scheduleExpiration":false,"expireAt":"2027-01-01T00:00:00Z","expireOnIdleTime":false,"idleTimeToExpire":"180.00:00:00"},
                 "tags":[],"auditEnabled":false}}
                """, Literals((main, "<main>")));
        }

        [Test]
        public async Task Create_WithAuditAndTags_ReturnsNormalisedTags()
        {
            var input = CreationInput(auditEnabled: true, tags: new List<string> { "Team:A", "beta", "team:a" });

            var response = await this.SecretMeta.Run(JsonRequest("post", input), "alpha", this.Owner, this.Logger);

            var main = await this.MainContentNameAsync("alpha");
            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"objectName":"alpha",
                 "content":[{"contentName":"<main>","isMain":true,"isImage":false,"contentType":"","fileName":"","isReady":false,"chunks":[]}],
                 "expirationSettings":{"scheduleExpiration":false,"expireAt":"2027-01-01T00:00:00Z","expireOnIdleTime":false,"idleTimeToExpire":"180.00:00:00"},
                 "tags":["team:a","beta"],"auditEnabled":true}}
                """, Literals((main, "<main>")));
        }

        [Test]
        public async Task Create_ExistingSecret_Conflict()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretMeta.Run(JsonRequest("post", CreationInput()), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Conflict, """
                {"status":"conflict","subStatus":"","error":"Secret 'alpha' already exists."}
                """);
        }

        [Test]
        public async Task Create_WithoutExpirationSettings_BadRequest()
        {
            var response = await this.SecretMeta.Run(TextRequest("post", "{}"), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Expiration settings are not provided."}
                """);
        }

        [Test]
        public async Task Create_InvalidSecretName_BadRequest()
        {
            var response = await this.SecretMeta.Run(JsonRequest("post", CreationInput()), "bad.name", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Secret id must contain only English letters, digits, '-' or '_'."}
                """);
        }

        [Test]
        public async Task Create_InvalidTag_BadRequest()
        {
            var input = CreationInput(tags: new List<string> { "Bad Tag!" });

            var response = await this.SecretMeta.Run(JsonRequest("post", input), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Tag 'Bad Tag!' must match ^[a-z0-9][a-z0-9:_-]*$."}
                """);
        }

        [Test]
        public async Task Read_ReturnsMetadata()
        {
            await this.CreateSecretAsync("alpha", tags: new List<string> { "team:a" });

            var response = await this.SecretMeta.Run(Request("get"), "alpha", this.Owner, this.Logger);

            var main = await this.MainContentNameAsync("alpha");
            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"objectName":"alpha",
                 "content":[{"contentName":"<main>","isMain":true,"isImage":false,"contentType":"","fileName":"","isReady":false,"chunks":[]}],
                 "expirationSettings":{"scheduleExpiration":false,"expireAt":"2027-01-01T00:00:00Z","expireOnIdleTime":false,"idleTimeToExpire":"180.00:00:00"},
                 "tags":["team:a"],"auditEnabled":false}}
                """, Literals((main, "<main>")));
        }

        [Test]
        public async Task Read_MissingSecret_NotFound()
        {
            var response = await this.SecretMeta.Run(Request("get"), "missing", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Secret 'missing' does not exist."}
                """);
        }

        [Test]
        public async Task Read_WithoutReadPermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretMeta.Run(Request("get"), "alpha", this.Stranger, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'Read' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task Read_PreviousVersionSecret_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            await this.MarkAsPreviousVersionAsync("alpha");

            var response = await this.SecretMeta.Run(Request("get"), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Cannot use this endpoint for previous versions data."}
                """);
        }

        [Test]
        public async Task Update_ReturnsUpdatedMetadata()
        {
            await this.CreateSecretAsync("alpha");
            var input = new MetadataUpdateInput
            {
                ExpirationSettings = new ExpirationSettingsInput
                {
                    ScheduleExpiration = true,
                    ExpireAt = FixedExpireAt,
                    ExpireOnIdleTime = true,
                    IdleTimeToExpire = TimeSpan.FromDays(1),
                },
                Tags = new List<string> { "B" },
            };

            var response = await this.SecretMeta.Run(JsonRequest("patch", input), "alpha", this.Owner, this.Logger);

            var main = await this.MainContentNameAsync("alpha");
            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"objectName":"alpha",
                 "content":[{"contentName":"<main>","isMain":true,"isImage":false,"contentType":"","fileName":"","isReady":false,"chunks":[]}],
                 "expirationSettings":{"scheduleExpiration":true,"expireAt":"2027-01-01T00:00:00Z","expireOnIdleTime":true,"idleTimeToExpire":"1.00:00:00"},
                 "tags":["b"],"auditEnabled":false}}
                """, Literals((main, "<main>")));
        }

        [Test]
        public async Task Update_WithoutWritePermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.SecretMeta.Run(JsonRequest("patch", CreationInput()), "alpha", this.Reader, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'Write' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task Delete_ReturnsOk()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretMeta.Run(Request("delete"), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":"ok"}
                """);
        }

        [Test]
        public async Task UnknownMethod_BadRequest()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretMeta.Run(Request("put"), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"error","subStatus":"","error":"Request method not recognized"}
                """);
        }

        [Test]
        public async Task ListV2_ReturnsDirectPermissionRowsWithTags()
        {
            await this.CreateSecretAsync("alpha");
            await this.CreateSecretAsync("beta", tags: new List<string> { "team:a" });

            var response = await this.SecretMeta.RunList(Request("get"), this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":[
                 {"objectName":"alpha","subjectType":0,"subjectName":"owner@contract.test","subjectId":"owner@contract.test","canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true,"tags":[]},
                 {"objectName":"beta","subjectType":0,"subjectName":"owner@contract.test","subjectId":"owner@contract.test","canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true,"tags":["team:a"]}]}
                """);
        }

        [Test]
        public async Task ListV2_TagFilter_ReturnsOnlyMatchingSecrets()
        {
            await this.CreateSecretAsync("alpha");
            await this.CreateSecretAsync("beta", tags: new List<string> { "team:a" });
            var request = Request("get");
            request.SetQueryString("tag=TEAM:A");

            var response = await this.SecretMeta.RunList(request, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":[
                 {"objectName":"beta","subjectType":0,"subjectName":"owner@contract.test","subjectId":"owner@contract.test","canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true,"tags":["team:a"]}]}
                """);
        }

        [Test]
        public async Task ListV2_ForReadOnlyGrantee_ReturnsPermissionRow()
        {
            await this.CreateSecretAsync("alpha", tags: new List<string> { "team:a" });
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.SecretMeta.RunList(Request("get"), this.Reader, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":[
                 {"objectName":"alpha","subjectType":0,"subjectName":"reader@contract.test","subjectId":"reader@contract.test","canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false,"tags":["team:a"]}]}
                """);
        }

        [Test]
        public async Task ListV3_ForReadOnlyGrantee_AddsCallerEffectivePermissions()
        {
            await this.CreateSecretAsync("alpha", tags: new List<string> { "team:a" });
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.SecretMeta.RunListV3(Request("get"), this.Reader, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":[
                 {"objectName":"alpha","subjectType":0,"subjectName":"reader@contract.test","subjectId":"reader@contract.test","canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false,"tags":["team:a"],
                  "callerEffectivePermissions":{"canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false}}]}
                """);
        }

        [Test]
        public async Task UnregisteredApplication_Forbidden()
        {
            var response = await this.SecretMeta.Run(JsonRequest("post", CreationInput()), "alpha", this.UnregisteredApp, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Application is not registered or disabled."}
                """);
        }

        [Test]
        public async Task DisabledApplication_ListV3_Forbidden()
        {
            await this.SeedApplicationAsync(AgentClientId, AgentDisplayName, enabled: false);

            var response = await this.SecretMeta.RunListV3(Request("get"), this.Agent, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Application is not registered or disabled."}
                """);
        }

        [Test]
        public async Task GlobalAccessGroupConfigured_CallerNotMember_Forbidden()
        {
            this.GlobalAccessGroups.AllowedGroups = "00000000-0000-0000-0000-0000000000d1";

            var response = await this.SecretMeta.RunListV3(Request("get"), this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Not a member of a global group."}
                """);
        }

        [Test]
        public async Task RegisteredApplication_ListV3_ShowsApplicationSubject()
        {
            await this.SeedApplicationAsync(AgentClientId, AgentDisplayName, enabled: true);
            await this.CreateSecretAsync("agent-secret", creator: this.Agent);

            var response = await this.SecretMeta.RunListV3(Request("get"), this.Agent, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":[
                 {"objectName":"agent-secret","subjectType":100,"subjectName":"contract-agent","subjectId":"contract-agent","canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true,"tags":[],
                  "callerEffectivePermissions":{"canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true}}]}
                """);
        }
    }
}
