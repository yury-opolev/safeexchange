/// <summary>
/// SecretAuditContractTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using NUnit.Framework;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Pins the wire contract of SafeExchangeSecretAudit (GET .../audit). Route-version agnostic.
    /// Event hashes depend on the random AuditInstanceId, so non-empty hash/prevHash values are masked.
    /// </summary>
    [TestFixture]
    public class SecretAuditContractTests : ContractTestBase
    {
        [Test]
        public async Task AuditDisabled_ReturnsDisabledPage()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretAudit.Run(Request("get"), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"auditEnabled":false,"events":[]}}
                """);
        }

        [Test]
        public async Task AuditEnabled_ReturnsEventPage()
        {
            await this.CreateSecretAsync("alpha", auditEnabled: true);
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.SecretAudit.Run(Request("get"), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"auditEnabled":true,"events":[
                 {"eventType":"SecretCreated","occurredAt":"2026-11-21T10:00:00Z","sequenceNumber":1,
                  "actor":{"subjectType":"User","subjectId":"owner@contract.test","subjectName":"owner@contract.test"},
                  "payload":{"tags":[],"expirationSettings":{"scheduleExpiration":false,"expireAt":"2027-01-01T00:00:00Z","expireOnIdleTime":false,"idleTimeToExpire":"180.00:00:00"},"auditEnabled":true},
                  "hash":"<hash>","prevHash":""},
                 {"eventType":"PermissionGranted","occurredAt":"2026-11-21T10:00:00Z","sequenceNumber":2,
                  "actor":{"subjectType":"User","subjectId":"owner@contract.test","subjectName":"owner@contract.test"},
                  "payload":{"target":{"subjectType":"User","subjectId":"reader@contract.test","subjectName":"reader@contract.test"},
                   "permissions":{"from":{"canRead":false,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false},
                                  "to":{"canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false}}},
                  "hash":"<hash>","prevHash":"<prevHash>"}]}}
                """, null, "hash", "prevHash");
        }

        [Test]
        public async Task AuditEnabled_PageSizeOne_ReturnsContinuation()
        {
            await this.CreateSecretAsync("alpha", auditEnabled: true);
            await this.GrantAsync("alpha", ReaderUpn, read: true);
            var request = Request("get");
            request.SetQueryString("pageSize=1");

            var response = await this.SecretAudit.Run(request, "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"auditEnabled":true,"events":[
                 {"eventType":"SecretCreated","occurredAt":"2026-11-21T10:00:00Z","sequenceNumber":1,
                  "actor":{"subjectType":"User","subjectId":"owner@contract.test","subjectName":"owner@contract.test"},
                  "payload":{"tags":[],"expirationSettings":{"scheduleExpiration":false,"expireAt":"2027-01-01T00:00:00Z","expireOnIdleTime":false,"idleTimeToExpire":"180.00:00:00"},"auditEnabled":true},
                  "hash":"<hash>","prevHash":""}],
                 "nextContinuation":"MQ=="}}
                """, null, "hash", "prevHash");
        }

        [Test]
        public async Task WithoutReadPermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha", auditEnabled: true);

            var response = await this.SecretAudit.Run(Request("get"), "alpha", this.Stranger, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'Read' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task MissingSecret_NotFound()
        {
            var response = await this.SecretAudit.Run(Request("get"), "missing", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Secret 'missing' does not exist."}
                """);
        }
    }
}
