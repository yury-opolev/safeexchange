/// <summary>
/// SecretAuditContractTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using NUnit.Framework;
    using System.Net;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Pins the wire contract of SafeExchangeSecretAudit (GET .../audit). Route-version agnostic.
    /// Event hashes depend on the random AuditInstanceId, so non-empty hash/prevHash values are masked.
    /// </summary>
    [TestFixture]
    public class SecretAuditContractTests : ContractTestBase
    {
        private static readonly byte[] Hello = Encoding.UTF8.GetBytes("hello");

        private static readonly byte[] World = Encoding.UTF8.GetBytes("world");

        private const string Created = """
            {"eventType":"SecretCreated","occurredAt":"2026-11-21T10:00:00Z","sequenceNumber":1,
             "actor":{"subjectType":"User","subjectId":"owner@contract.test","subjectName":"owner@contract.test"},
             "payload":{"tags":[],"expirationSettings":{"scheduleExpiration":false,"expireAt":"2027-01-01T00:00:00Z","expireOnIdleTime":false,"idleTimeToExpire":"180.00:00:00"},"auditEnabled":true},
             "hash":"<hash>","prevHash":""}
            """;

        private const string FirstChunkWritten = """
            {"eventType":"ContentWritten","occurredAt":"2026-11-21T10:00:00Z","sequenceNumber":2,
             "actor":{"subjectType":"User","subjectId":"owner@contract.test","subjectName":"owner@contract.test"},
             "payload":{"contentId":"<att>","chunkId":"<att>-00000000"},"hash":"<hash>","prevHash":"<prevHash>"}
            """;

        private const string SecondChunkWritten = """
            {"eventType":"ContentWritten","occurredAt":"2026-11-21T10:00:00Z","sequenceNumber":3,
             "actor":{"subjectType":"User","subjectId":"owner@contract.test","subjectName":"owner@contract.test"},
             "payload":{"contentId":"<att>","chunkId":"<att>-00000001"},"hash":"<hash>","prevHash":"<prevHash>"}
            """;

        private const string ReaderGranted = """
            {"eventType":"PermissionGranted","occurredAt":"2026-11-21T10:00:00Z","sequenceNumber":4,
             "actor":{"subjectType":"User","subjectId":"owner@contract.test","subjectName":"owner@contract.test"},
             "payload":{"target":{"subjectType":"User","subjectId":"reader@contract.test","subjectName":"reader@contract.test"},
              "permissions":{"from":{"canRead":false,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false},
                             "to":{"canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false}}},
             "hash":"<hash>","prevHash":"<prevHash>"}
            """;

        private const string OwnerReadsMerged = """
            {"eventType":"ContentRead","occurredAt":"2026-11-21T10:00:00Z","firstAt":"2026-11-21T10:00:00Z","lastAt":"2026-11-21T10:00:00Z",
             "sequenceNumber":5,"sequenceFrom":5,"sequenceTo":8,
             "actor":{"subjectType":"User","subjectId":"owner@contract.test","subjectName":"owner@contract.test"},
             "contentId":"<att>","chunkIds":["<att>-00000000","<att>-00000001","<att>-00000000","<att>-00000001"],
             "hash":"<hash>","prevHash":"<prevHash>"}
            """;

        private const string OwnerLastReadMerged = """
            {"eventType":"ContentRead","occurredAt":"2026-11-21T10:00:00Z","firstAt":"2026-11-21T10:00:00Z","lastAt":"2026-11-21T10:00:00Z",
             "sequenceNumber":8,"sequenceFrom":8,"sequenceTo":8,
             "actor":{"subjectType":"User","subjectId":"owner@contract.test","subjectName":"owner@contract.test"},
             "contentId":"<att>","chunkIds":["<att>-00000001"],
             "hash":"<hash>","prevHash":"<prevHash>"}
            """;

        private const string ReaderReadsMerged = """
            {"eventType":"ContentRead","occurredAt":"2026-11-21T10:00:00Z","firstAt":"2026-11-21T10:00:00Z","lastAt":"2026-11-21T10:00:00Z",
             "sequenceNumber":9,"sequenceFrom":9,"sequenceTo":10,
             "actor":{"subjectType":"User","subjectId":"reader@contract.test","subjectName":"reader@contract.test"},
             "contentId":"<att>","chunkIds":["<att>-00000000","<att>-00000001"],
             "hash":"<hash>","prevHash":"<prevHash>"}
            """;

        private const string OwnerRawRead5 = """
            {"eventType":"ContentRead","occurredAt":"2026-11-21T10:00:00Z","sequenceNumber":5,
             "actor":{"subjectType":"User","subjectId":"owner@contract.test","subjectName":"owner@contract.test"},
             "payload":{"contentId":"<att>","chunkId":"<att>-00000000"},"hash":"<hash>","prevHash":"<prevHash>"}
            """;

        private const string OwnerRawRead6 = """
            {"eventType":"ContentRead","occurredAt":"2026-11-21T10:00:00Z","sequenceNumber":6,
             "actor":{"subjectType":"User","subjectId":"owner@contract.test","subjectName":"owner@contract.test"},
             "payload":{"contentId":"<att>","chunkId":"<att>-00000001"},"hash":"<hash>","prevHash":"<prevHash>"}
            """;

        private const string OwnerRawRead7 = """
            {"eventType":"ContentRead","occurredAt":"2026-11-21T10:00:00Z","sequenceNumber":7,
             "actor":{"subjectType":"User","subjectId":"owner@contract.test","subjectName":"owner@contract.test"},
             "payload":{"contentId":"<att>","chunkId":"<att>-00000000"},"hash":"<hash>","prevHash":"<prevHash>"}
            """;

        private const string OwnerRawRead8 = """
            {"eventType":"ContentRead","occurredAt":"2026-11-21T10:00:00Z","sequenceNumber":8,
             "actor":{"subjectType":"User","subjectId":"owner@contract.test","subjectName":"owner@contract.test"},
             "payload":{"contentId":"<att>","chunkId":"<att>-00000001"},"hash":"<hash>","prevHash":"<prevHash>"}
            """;

        private const string ReaderRawRead9 = """
            {"eventType":"ContentRead","occurredAt":"2026-11-21T10:00:00Z","sequenceNumber":9,
             "actor":{"subjectType":"User","subjectId":"reader@contract.test","subjectName":"reader@contract.test"},
             "payload":{"contentId":"<att>","chunkId":"<att>-00000000"},"hash":"<hash>","prevHash":"<prevHash>"}
            """;

        private const string ReaderRawRead10 = """
            {"eventType":"ContentRead","occurredAt":"2026-11-21T10:00:00Z","sequenceNumber":10,
             "actor":{"subjectType":"User","subjectId":"reader@contract.test","subjectName":"reader@contract.test"},
             "payload":{"contentId":"<att>","chunkId":"<att>-00000001"},"hash":"<hash>","prevHash":"<prevHash>"}
            """;

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

            ContractAssert.AuditHashChain(response);
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

            ContractAssert.AuditHashChain(response);
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

        [Test]
        public async Task ContentReads_DefaultPage_MergesRunsPerActor()
        {
            var attachment = await this.SeedContentReadScenarioAsync();

            var response = await this.SecretAudit.Run(Request("get"), "alpha", this.Owner, this.Logger);

            ContractAssert.AuditHashChain(response);
            ContractAssert.Json(response, HttpStatusCode.OK, $$"""
                {"status":"ok","subStatus":"","result":{"auditEnabled":true,"events":[
                 {{Created}},
                 {{FirstChunkWritten}},
                 {{SecondChunkWritten}},
                 {{ReaderGranted}},
                 {{OwnerReadsMerged}},
                 {{ReaderReadsMerged}}
                ]}
                }
                """, Literals((attachment, "<att>")), "hash", "prevHash");
        }

        [Test]
        public async Task ContentReads_Raw_ReturnsEveryEvent()
        {
            var attachment = await this.SeedContentReadScenarioAsync();
            var request = Request("get");
            request.SetQueryString("raw=true");

            var response = await this.SecretAudit.Run(request, "alpha", this.Owner, this.Logger);

            ContractAssert.AuditHashChain(response);
            ContractAssert.Json(response, HttpStatusCode.OK, $$"""
                {"status":"ok","subStatus":"","result":{"auditEnabled":true,"events":[
                 {{Created}},
                 {{FirstChunkWritten}},
                 {{SecondChunkWritten}},
                 {{ReaderGranted}},
                 {{OwnerRawRead5}},
                 {{OwnerRawRead6}},
                 {{OwnerRawRead7}},
                 {{OwnerRawRead8}},
                 {{ReaderRawRead9}},
                 {{ReaderRawRead10}}
                ]}
                }
                """, Literals((attachment, "<att>")), "hash", "prevHash");
        }

        [Test]
        public async Task ContentReads_Descending_ReversesMergedPage()
        {
            var attachment = await this.SeedContentReadScenarioAsync();
            var request = Request("get");
            request.SetQueryString("direction=desc");

            var response = await this.SecretAudit.Run(request, "alpha", this.Owner, this.Logger);

            ContractAssert.AuditHashChain(response, descending: true);
            ContractAssert.Json(response, HttpStatusCode.OK, $$"""
                {"status":"ok","subStatus":"","result":{"auditEnabled":true,"events":[
                 {{ReaderReadsMerged}},
                 {{OwnerReadsMerged}},
                 {{ReaderGranted}},
                 {{SecondChunkWritten}},
                 {{FirstChunkWritten}},
                 {{Created}}
                ]}
                }
                """, Literals((attachment, "<att>")), "hash", "prevHash");
        }

        [Test]
        public async Task ContentReads_Continuation_ReturnsEventsAfterSequence()
        {
            var attachment = await this.SeedContentReadScenarioAsync();
            var request = Request("get");
            request.SetQueryString("continuation=MQ==");

            var response = await this.SecretAudit.Run(request, "alpha", this.Owner, this.Logger);

            ContractAssert.AuditHashChain(response);
            ContractAssert.Json(response, HttpStatusCode.OK, $$"""
                {"status":"ok","subStatus":"","result":{"auditEnabled":true,"events":[
                 {{FirstChunkWritten}},
                 {{SecondChunkWritten}},
                 {{ReaderGranted}},
                 {{OwnerReadsMerged}},
                 {{ReaderReadsMerged}}
                ]}
                }
                """, Literals((attachment, "<att>")), "hash", "prevHash");
        }

        [Test]
        public async Task ContentReads_PageSizeOne_ReturnsNextContinuation()
        {
            var attachment = await this.SeedContentReadScenarioAsync();
            var request = Request("get");
            request.SetQueryString("pageSize=1");

            var response = await this.SecretAudit.Run(request, "alpha", this.Owner, this.Logger);

            ContractAssert.AuditHashChain(response);
            ContractAssert.Json(response, HttpStatusCode.OK, $$"""
                {"status":"ok","subStatus":"","result":{"auditEnabled":true,"events":[
                 {{Created}}
                ],"nextContinuation":"MQ=="}
                }
                """, Literals((attachment, "<att>")), "hash", "prevHash");
        }

        [Test]
        public async Task ContentReads_DescendingPageBoundary_SplitsMergedRun()
        {
            var attachment = await this.SeedContentReadScenarioAsync();
            var request = Request("get");
            request.SetQueryString("direction=desc&pageSize=3");

            var response = await this.SecretAudit.Run(request, "alpha", this.Owner, this.Logger);

            // The page holds raw events 10, 9 and 8; the owner's run 5-8 is split at the page boundary.
            ContractAssert.AuditHashChain(response, descending: true);
            ContractAssert.Json(response, HttpStatusCode.OK, $$"""
                {"status":"ok","subStatus":"","result":{"auditEnabled":true,"events":[
                 {{ReaderReadsMerged}},
                 {{OwnerLastReadMerged}}
                ],"nextContinuation":"OA=="}
                }
                """, Literals((attachment, "<att>")), "hash", "prevHash");
        }

        // Audit sequence: 1 SecretCreated, 2-3 ContentWritten, 4 PermissionGranted,
        // 5-6 owner chunk reads, 7-8 owner /all reads, 9-10 reader /all reads.
        private async Task<string> SeedContentReadScenarioAsync()
        {
            await this.CreateSecretAsync("alpha", auditEnabled: true);
            var attachment = await this.CreateAttachmentAsync("alpha");
            var ticket = TicketOf(await this.UploadChunkAsync("alpha", attachment, Hello, interim: true));
            var final = await this.UploadChunkAsync("alpha", attachment, World, ticket: ticket);
            Assert.That(final.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Arrange: final chunk failed.");
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            foreach (var index in new[] { 0, 1 })
            {
                var chunk = await this.SecretStream.Run(Request("get"), "alpha", attachment, $"{attachment}-{index:00000000}", this.Owner, this.Logger);
                Assert.That(chunk.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Arrange: chunk download failed.");
            }

            foreach (var caller in new[] { this.Owner, this.Reader })
            {
                var all = await this.SecretStream.RunContentDownload(Request("get"), "alpha", attachment, caller, this.Logger);
                Assert.That(all.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Arrange: content download failed.");
            }

            return attachment;
        }
    }
}
