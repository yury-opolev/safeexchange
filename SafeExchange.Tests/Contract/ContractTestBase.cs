/// <summary>
/// ContractTestBase
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using Azure.Core.Serialization;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;
    using Microsoft.IdentityModel.Tokens;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Groups;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Services;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;

    /// <summary>
    /// Shared fixture plumbing for the REST contract (characterization) tests. Every test
    /// gets a fresh EF InMemory database, a fixed clock, the production response serializer
    /// settings and freshly constructed handlers. Handler construction lives only here so
    /// later phases change plumbing in one place.
    /// </summary>
    public abstract class ContractTestBase
    {
        protected const string TenantId = "00000000-0000-0000-0000-000000000001";

        protected const string OwnerUpn = "owner@contract.test";

        protected const string ReaderUpn = "reader@contract.test";

        protected const string StrangerUpn = "stranger@contract.test";

        protected const string MixedCaseOwnerUpn = "Owner@Contract.Test";

        protected const string AgentClientId = "00000000-0000-0000-0000-0000000000c1";

        protected const string AgentDisplayName = "contract-agent";

        protected const string UnregisteredClientId = "00000000-0000-0000-0000-0000000000c9";

        protected static readonly DateTime FixedNow = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);

        protected static readonly DateTime FixedExpireAt = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private IServiceProvider previousInstanceServices = null!;

        private DbContextOptions<SafeExchangeDbContext> dbContextOptions = null!;

        private bool previousUseSpecifiedDateTime;

        private DateTime previousSpecifiedDateTime;

        protected ILogger Logger { get; private set; } = null!;

        protected SafeExchangeDbContext DbContext { get; private set; } = null!;

        protected TestBlobHelper BlobHelper { get; private set; } = null!;

        /// <summary>
        /// The live GlobalAccessFilter configuration (read per request); empty means no global group gate.
        /// </summary>
        protected GloballyAllowedGroupsConfiguration GlobalAccessGroups { get; private set; } = null!;

        protected SafeExchangeSecretMeta SecretMeta { get; private set; } = null!;

        protected SafeExchangeSecretContentMeta ContentMeta { get; private set; } = null!;

        protected SafeExchangeSecretStream SecretStream { get; private set; } = null!;

        protected SafeExchangeContentCommit ContentCommit { get; private set; } = null!;

        protected SafeExchangeAccess SecretAccess { get; private set; } = null!;

        protected SafeExchangeSecretAudit SecretAudit { get; private set; } = null!;

        protected ClaimsPrincipal Owner => UserPrincipal(OwnerUpn, "00000000-0000-0000-0000-0000000000a1");

        protected ClaimsPrincipal Reader => UserPrincipal(ReaderUpn, "00000000-0000-0000-0000-0000000000a2");

        protected ClaimsPrincipal Stranger => UserPrincipal(StrangerUpn, "00000000-0000-0000-0000-0000000000a3");

        protected ClaimsPrincipal MixedCaseOwner => UserPrincipal(MixedCaseOwnerUpn, "00000000-0000-0000-0000-0000000000a4");

        protected ClaimsPrincipal Agent => AppPrincipal(AgentClientId);

        protected ClaimsPrincipal UnregisteredApp => AppPrincipal(UnregisteredClientId);

        [SetUp]
        public void ContractSetUp()
        {
            this.previousInstanceServices = TestFactory.FunctionContext.InstanceServices;
            this.previousUseSpecifiedDateTime = DateTimeProvider.UseSpecifiedDateTime;
            this.previousSpecifiedDateTime = DateTimeProvider.SpecifiedDateTime;

            DateTimeProvider.SpecifiedDateTime = FixedNow;
            DateTimeProvider.UseSpecifiedDateTime = true;

            // Mirrors production: the worker's JsonObjectSerializer is built from the
            // JsonSerializerOptions configured in SafeExchangeStartup.ConfigureServices
            // (plus the worker default PropertyNameCaseInsensitive, which only affects reads).
            var serializer = new JsonObjectSerializer(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNameCaseInsensitive = true,
            });
            var workerOptions = Options.Create(new WorkerOptions { Serializer = serializer });
            var serviceProvider = new Mock<IServiceProvider>();
            serviceProvider
                .Setup(x => x.GetService(typeof(IOptions<WorkerOptions>)))
                .Returns(workerOptions);
            TestFactory.FunctionContext.InstanceServices = serviceProvider.Object;

            this.Logger = TestFactory.CreateLogger();

            this.dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"{this.GetType().Name}-{Guid.NewGuid()}")
                .Options;
            this.DbContext = new SafeExchangeDbContext(this.dbContextOptions);
            this.BlobHelper = new TestBlobHelper();
            this.GlobalAccessGroups = new GloballyAllowedGroupsConfiguration { AllowedGroups = string.Empty };

            this.BuildHandlers();
        }

        /// <summary>
        /// Rebuilds the handlers over the same database, blob store and global-group
        /// configuration with configuration overrides (handlers bind Features and AccessTickets
        /// once at construction), e.g. <c>("Features:AllowLegacyAttachmentUploads", "False")</c>.
        /// </summary>
        protected void BuildHandlers(params (string Key, string Value)[] overrides)
        {
            var settings = new Dictionary<string, string?>
            {
                { "Features:UseGroupsAuthorization", "False" },
                { "AccessTickets:AccessTicketTimeout", "00:10:00" },
            };
            foreach (var (key, value) in overrides)
            {
                settings[key] = value;
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings)
                .Build();

            var dbContextOptions = this.dbContextOptions;
            var tokenHelper = new TokenHelper(configuration, NullLogger<TokenHelper>.Instance);

            var allowedGroups = this.GlobalAccessGroups;
            var callerResolver = new CallerResolver(
                Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == allowedGroups),
                tokenHelper,
                this.DbContext,
                NullLogger<CallerResolver>.Instance);

            var purger = new PurgeManager(configuration, this.BlobHelper, NullLogger<PurgeManager>.Instance);
            var permissionsManager = new PermissionsManager(configuration, this.DbContext, NullLogger<PermissionsManager>.Instance);
            var groupsManager = new GroupsManager(this.DbContext, NullLogger<GroupsManager>.Instance);
            var features = new Features();
            var orphanedSecretConfiguration = new OrphanedSecretConfiguration();
            var orphanedSecretManager = new OrphanedSecretManager(
                Mock.Of<IOptionsMonitor<Features>>(x => x.CurrentValue == features),
                Mock.Of<IOptionsMonitor<OrphanedSecretConfiguration>>(x => x.CurrentValue == orphanedSecretConfiguration),
                NullLogger<OrphanedSecretManager>.Instance);
            var auditWriter = new AuditWriter(new ContractDbContextFactory(dbContextOptions), NullLogger<AuditWriter>.Instance);

            var secretService = new SecretService(configuration, this.DbContext, purger, permissionsManager, auditWriter, NullLogger<SecretService>.Instance);
            var contentService = new ContentService(configuration, this.DbContext, purger, permissionsManager, auditWriter, NullLogger<ContentService>.Instance);
            var chunkService = new ChunkService(configuration, this.DbContext, purger, this.BlobHelper, permissionsManager, auditWriter, NullLogger<ChunkService>.Instance);
            var accessService = new AccessService(this.DbContext, groupsManager, permissionsManager, auditWriter, orphanedSecretManager, NullLogger<AccessService>.Instance);
            var auditQueryService = new AuditQueryService(this.DbContext, permissionsManager);

            this.SecretMeta = new SafeExchangeSecretMeta(callerResolver, secretService);
            this.ContentMeta = new SafeExchangeSecretContentMeta(callerResolver, contentService);
            this.SecretStream = new SafeExchangeSecretStream(callerResolver, chunkService);
            this.ContentCommit = new SafeExchangeContentCommit(callerResolver, contentService);
            this.SecretAccess = new SafeExchangeAccess(callerResolver, accessService);
            this.SecretAudit = new SafeExchangeSecretAudit(callerResolver, auditQueryService);
        }

        [TearDown]
        public void ContractTearDown()
        {
            this.DbContext.Database.EnsureDeleted();
            this.DbContext.Dispose();

            // TestFactory.FunctionContext and DateTimeProvider are process-wide; restore them so
            // fixtures that configure them once in OneTimeSetUp are unaffected by these tests.
            TestFactory.FunctionContext.InstanceServices = this.previousInstanceServices;
            DateTimeProvider.SpecifiedDateTime = this.previousSpecifiedDateTime;
            DateTimeProvider.UseSpecifiedDateTime = this.previousUseSpecifiedDateTime;
        }

        protected static ClaimsPrincipal UserPrincipal(string upn, string objectId)
            => new(new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new("upn", upn),
                new("oid", objectId),
                new("tid", TenantId),
            }.AsEnumerable()));

        // App-only (client-credentials) v2 token shape: azp + azpacr and no scope claim.
        protected static ClaimsPrincipal AppPrincipal(string clientId)
            => new(new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new("azp", clientId),
                new("azpacr", "1"),
                new("oid", "00000000-0000-0000-0000-0000000000b1"),
                new("tid", TenantId),
            }.AsEnumerable()));

        protected static TestHttpRequestData Request(string method)
        {
            var request = TestFactory.CreateHttpRequestData(method);
            request.SetBodyAsStream(new MemoryStream());
            return request;
        }

        protected static TestHttpRequestData JsonRequest<T>(string method, T body)
        {
            var request = TestFactory.CreateHttpRequestData(method);
            request.SetBodyAsJson(body);
            return request;
        }

        protected static TestHttpRequestData TextRequest(string method, string body)
            => BytesRequest(method, Encoding.UTF8.GetBytes(body));

        protected static TestHttpRequestData BytesRequest(string method, byte[] body)
        {
            var request = TestFactory.CreateHttpRequestData(method);
            request.SetBodyAsStream(new MemoryStream(body));
            return request;
        }

        protected static MetadataCreationInput CreationInput(bool? auditEnabled = null, List<string>? tags = null)
            => new()
            {
                ExpirationSettings = new ExpirationSettingsInput
                {
                    ScheduleExpiration = false,
                    ExpireAt = FixedExpireAt,
                    ExpireOnIdleTime = false,
                    IdleTimeToExpire = TimeSpan.FromDays(180),
                },
                Tags = tags,
                AuditEnabled = auditEnabled,
            };

        protected static IReadOnlyDictionary<string, string> Literals(params (string Value, string Token)[] pairs)
            => pairs.ToDictionary(p => p.Value, p => p.Token);

        protected static JsonNode ReadJson(HttpResponseData response)
            => JsonNode.Parse(ContractAssert.AsTest(response).ReadBodyAsString())
                ?? throw new InvalidOperationException("Response body is the JSON literal null.");

        protected static string Sha256Hex(byte[] data)
            => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        protected async Task CreateSecretAsync(string secretId, ClaimsPrincipal? creator = null, bool? auditEnabled = null, List<string>? tags = null)
        {
            var response = await this.SecretMeta.Run(
                JsonRequest("post", CreationInput(auditEnabled, tags)), secretId, creator ?? this.Owner, this.Logger);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Arrange: secret creation failed.");
        }

        protected async Task GrantAsync(
            string secretId,
            string subjectName,
            bool read,
            bool write = false,
            bool grant = false,
            bool revoke = false,
            SubjectTypeInput subjectType = SubjectTypeInput.User,
            ClaimsPrincipal? actor = null)
        {
            var input = new List<SubjectPermissionsInput>
            {
                new()
                {
                    SubjectType = subjectType,
                    SubjectName = subjectName,
                    CanRead = read,
                    CanWrite = write,
                    CanGrantAccess = grant,
                    CanRevokeAccess = revoke,
                },
            };
            var response = await this.SecretAccess.Run(JsonRequest("post", input), secretId, actor ?? this.Owner, this.Logger);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Arrange: grant failed.");
        }

        protected async Task<string> MainContentNameAsync(string secretId)
        {
            var secret = await this.DbContext.Objects.FirstAsync(o => o.ObjectName == secretId);
            return secret.Content.Single(c => c.IsMain).ContentName;
        }

        protected async Task<string> CreateAttachmentAsync(string secretId, string contentType = "application/pdf", string fileName = "report.pdf")
        {
            var input = new ContentMetadataCreationInput { ContentType = contentType, FileName = fileName, IsImage = false };
            var response = await this.ContentMeta.Run(JsonRequest("post", input), secretId, string.Empty, this.Owner, this.Logger);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Arrange: attachment creation failed.");
            return ReadJson(response)["result"]!["contentName"]!.GetValue<string>();
        }

        protected async Task<HttpResponseData> UploadChunkAsync(
            string secretId,
            string contentId,
            byte[] data,
            string? ticket = null,
            bool interim = false,
            string? chunkHash = null,
            ClaimsPrincipal? caller = null)
        {
            var request = BytesRequest("post", data);
            if (!string.IsNullOrEmpty(ticket))
            {
                request.Headers.Add(SafeExchangeSecretStream.AccessTicketHeaderName, ticket);
            }

            if (interim)
            {
                request.Headers.Add(SafeExchangeSecretStream.OperationTypeHeaderName, SafeExchangeSecretStream.InterimOperationType);
            }

            if (chunkHash is not null)
            {
                request.Headers.Add(SafeExchangeSecretStream.ChunkHashHeaderName, chunkHash);
                request.Headers.Add("Content-Length", data.Length.ToString());
            }

            return await this.SecretStream.Run(request, secretId, contentId, string.Empty, caller ?? this.Owner, this.Logger);
        }

        protected static string TicketOf(HttpResponseData response)
            => ReadJson(response)["result"]!["accessTicket"]!.GetValue<string>();

        protected async Task MarkAsPreviousVersionAsync(string secretId)
        {
            var secret = await this.DbContext.Objects.FirstAsync(o => o.ObjectName == secretId);
            secret.KeepInStorage = false;
            await this.DbContext.SaveChangesAsync();
        }

        protected async Task SeedApplicationAsync(string clientId, string displayName, bool enabled)
        {
            this.DbContext.Applications.Add(new Application
            {
                Id = Guid.NewGuid().ToString(),
                PartitionKey = Application.DefaultPartitionKey,
                Enabled = enabled,
                DisplayName = displayName,
                ContactEmail = "agent-owner@contract.test",
                AadClientId = clientId,
                AadTenantId = TenantId,
                CreatedAt = FixedNow,
                CreatedBy = "contract-test",
                ModifiedAt = DateTime.MinValue,
                ModifiedBy = string.Empty,
            });
            await this.DbContext.SaveChangesAsync();
        }

        protected async Task SeedUserAsync(string upn, string objectId, bool consentRequired = false, params string[] groupIds)
        {
            this.DbContext.Users.Add(new User("Contract User", objectId, TenantId, upn, upn)
            {
                ConsentRequired = consentRequired,
                Groups = groupIds.Select(g => new UserGroup { AadGroupId = g }).ToList(),
            });
            await this.DbContext.SaveChangesAsync();
        }

        private sealed class ContractDbContextFactory : IDbContextFactory<SafeExchangeDbContext>
        {
            private readonly DbContextOptions<SafeExchangeDbContext> options;

            public ContractDbContextFactory(DbContextOptions<SafeExchangeDbContext> options)
            {
                this.options = options ?? throw new ArgumentNullException(nameof(options));
            }

            public SafeExchangeDbContext CreateDbContext() => new(this.options);
        }
    }
}
