/// <summary>
/// AuditQueryServiceTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using NUnit.Framework;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using System;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    [TestFixture]
    public class AuditQueryServiceTests : ServiceTestBase
    {
        private AuditQueryService service = null!;

        [SetUp]
        public void SetUp()
        {
            this.service = new AuditQueryService(this.DbContext, this.PermissionsManager);
        }

        [Test]
        public async Task Read_CallerWithoutRead_IsForbidden()
        {
            await this.CreateSecretAsync("alpha", auditEnabled: true);

            var result = await this.service.ReadAsync(this.Stranger, "alpha", new AuditPageQuery());

            AssertInsufficientPermissions(result.Outcome, PermissionType.Read, "alpha");
        }

        [Test]
        public async Task Read_Missing_IsNotFound()
        {
            var result = await this.service.ReadAsync(this.Owner, "missing", new AuditPageQuery());

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.NotFound));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(result.Error, Is.EqualTo("Secret 'missing' does not exist."));
        }

        [Test]
        public async Task Read_AuditDisabled_ReturnsAnEmptyDisabledPage()
        {
            await this.CreateSecretAsync("alpha");

            var result = await this.service.ReadAsync(this.Owner, "alpha", new AuditPageQuery());

            Assert.That(result.IsOk, Is.True);
            Assert.That(result.Value!.AuditEnabled, Is.False);
            Assert.That(result.Value!.Events, Is.Empty);
        }

        [Test]
        public async Task Read_PagesWithAContinuationToken()
        {
            await this.CreateSecretAsync("alpha", auditEnabled: true);
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);
            await this.CreateSecretService().UpdateAsync(this.Owner, "alpha", new MetadataUpdateInput { ExpirationSettings = CreationInput().ExpirationSettings, Tags = new() { "x" } });

            var first = await this.service.ReadAsync(this.Reader, "alpha", new AuditPageQuery { PageSize = 1 });
            var second = await this.service.ReadAsync(this.Reader, "alpha", new AuditPageQuery { PageSize = 1, Continuation = first.Value!.NextContinuation });

            Assert.That(first.Value!.Events.Single().EventType, Is.EqualTo(nameof(SecretAuditEventType.SecretCreated)));
            Assert.That(first.Value!.NextContinuation, Is.Not.Null);
            Assert.That(second.Value!.Events.Single().EventType, Is.EqualTo(nameof(SecretAuditEventType.SecretMetadataUpdated)));
            Assert.That(second.Value!.NextContinuation, Is.Null);
        }

        [Test]
        public void Parse_ReadsTheRestQueryString()
        {
            var query = AuditPageQuery.Parse("?from=2026-01-01T00:00:00Z&to=2026-02-01&raw=true&pageSize=7&continuation=MTA%3D&direction=DESC");

            Assert.That(query.From, Is.EqualTo(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(query.To, Is.EqualTo(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(query.Raw, Is.True);
            Assert.That(query.PageSize, Is.EqualTo(7));
            Assert.That(query.Continuation, Is.EqualTo("MTA="));
            Assert.That(query.Descending, Is.True);
            Assert.That(AuditPageQuery.Parse(null), Is.EqualTo(new AuditPageQuery()));
        }
    }
}
