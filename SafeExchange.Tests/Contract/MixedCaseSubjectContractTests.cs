/// <summary>
/// MixedCaseSubjectContractTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using NUnit.Framework;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Pins how a caller whose UPN has upper-case characters is represented on the wire.
    /// SetPermissionAsync lower-cases the stored SubjectId (SubjectName keeps the raw case); the
    /// v2 list queries with the raw token UPN while the v3 list and every authorization check
    /// normalise it.
    /// </summary>
    [TestFixture]
    public class MixedCaseSubjectContractTests : ContractTestBase
    {
        [Test]
        public async Task ListV2_ForMixedCaseCreator_ReturnsEmptyList()
        {
            await this.CreateSecretAsync("alpha", creator: this.MixedCaseOwner);

            var response = await this.SecretMeta.RunList(Request("get"), this.MixedCaseOwner, this.Logger);

            // Quirk: v2 compares the raw token UPN with the lower-cased stored SubjectId, so the creator's own secret is missing.
            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":[]}
                """);
        }

        [Test]
        public async Task ListV3_ForMixedCaseCreator_ReturnsRawCaseSubject()
        {
            await this.CreateSecretAsync("alpha", creator: this.MixedCaseOwner);

            var response = await this.SecretMeta.RunListV3(Request("get"), this.MixedCaseOwner, this.Logger);

            // Quirk: v3 echoes the raw token UPN for both subjectName and subjectId.
            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":[
                 {"objectName":"alpha","subjectType":0,"subjectName":"Owner@Contract.Test","subjectId":"Owner@Contract.Test","canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true,"tags":[],
                  "callerEffectivePermissions":{"canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true}}]}
                """);
        }

        [Test]
        public async Task Access_ForMixedCaseCreator_KeepsRawSubjectNameAndLowerCasedSubjectId()
        {
            await this.CreateSecretAsync("alpha", creator: this.MixedCaseOwner);

            var response = await this.SecretAccess.Run(Request("get"), "alpha", this.MixedCaseOwner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":[
                 {"objectName":"alpha","subjectType":0,"subjectName":"Owner@Contract.Test","subjectId":"owner@contract.test","canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true,"tags":[]}]}
                """);
        }

        [Test]
        public async Task Read_ForMixedCaseCreator_IsAuthorized()
        {
            await this.CreateSecretAsync("alpha", creator: this.MixedCaseOwner);

            var response = await this.SecretMeta.Run(Request("get"), "alpha", this.MixedCaseOwner, this.Logger);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task Audit_ForMixedCaseCreator_KeepsRawCaseActor()
        {
            await this.CreateSecretAsync("alpha", creator: this.MixedCaseOwner, auditEnabled: true);

            var response = await this.SecretAudit.Run(Request("get"), "alpha", this.MixedCaseOwner, this.Logger);

            ContractAssert.AuditHashChain(response);
            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"auditEnabled":true,"events":[
                 {"eventType":"SecretCreated","occurredAt":"2026-11-21T10:00:00Z","sequenceNumber":1,
                  "actor":{"subjectType":"User","subjectId":"Owner@Contract.Test","subjectName":"Owner@Contract.Test"},
                  "payload":{"tags":[],"expirationSettings":{"scheduleExpiration":false,"expireAt":"2027-01-01T00:00:00Z","expireOnIdleTime":false,"idleTimeToExpire":"180.00:00:00"},"auditEnabled":true},
                  "hash":"<hash>","prevHash":""}]}}
                """, null, "hash", "prevHash");
        }
    }
}
