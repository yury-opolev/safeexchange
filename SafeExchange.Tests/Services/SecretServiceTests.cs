/// <summary>
/// SecretServiceTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging.Abstractions;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Services;
    using System;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    [TestFixture]
    public class SecretServiceTests : ServiceTestBase
    {
        private SecretService service = null!;

        [SetUp]
        public void SetUp()
        {
            this.service = this.CreateSecretService();
        }

        [Test]
        public async Task Create_GivesTheCallerFullPermissions_AndGetReturnsTheMetadata()
        {
            var created = await this.service.CreateAsync(this.Owner, "alpha", CreationInput());
            var read = await this.service.GetAsync(this.Owner, "alpha");

            Assert.That(created.IsOk, Is.True);
            Assert.That(created.Value!.ObjectName, Is.EqualTo("alpha"));
            Assert.That(read.IsOk, Is.True);
            Assert.That(read.Value!.ObjectName, Is.EqualTo("alpha"));
            Assert.That(await this.PermissionsManager.IsAuthorizedAsync(SubjectType.User, OwnerUpn, "alpha", PermissionType.Full), Is.True);
        }

        [Test]
        public async Task Create_Existing_IsConflict()
        {
            await this.CreateSecretAsync("alpha");

            var result = await this.service.CreateAsync(this.Stranger, "alpha", CreationInput());

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.Conflict));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(result.Error, Is.EqualTo("Secret 'alpha' already exists."));
        }

        [Test]
        public async Task Create_WithoutInput_IsBadRequest()
        {
            var result = await this.service.CreateAsync(this.Owner, "alpha", null);

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.BadRequest));
            Assert.That(result.Error, Is.EqualTo("Expiration settings are not provided."));
        }

        [Test]
        public async Task Get_Missing_IsNotFound()
        {
            var result = await this.service.GetAsync(this.Owner, "missing");

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.NotFound));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(result.Error, Is.EqualTo("Secret 'missing' does not exist."));
        }

        [Test]
        public async Task Get_CallerWithoutRead_IsForbidden()
        {
            await this.CreateSecretAsync("alpha");

            var result = await this.service.GetAsync(this.Stranger, "alpha");

            AssertInsufficientPermissions(result.Outcome, PermissionType.Read, "alpha");
        }

        [Test]
        public async Task Update_ReaderWithoutWrite_IsForbidden()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);

            var result = await this.service.UpdateAsync(this.Reader, "alpha", new MetadataUpdateInput { ExpirationSettings = CreationInput().ExpirationSettings });

            AssertInsufficientPermissions(result.Outcome, PermissionType.Write, "alpha");
        }

        [Test]
        public async Task Delete_ReaderWithoutWrite_IsForbidden_AndKeepsTheSecret()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);

            var result = await this.service.DeleteAsync(this.Reader, "alpha");

            AssertInsufficientPermissions(result, PermissionType.Write, "alpha");
            Assert.That(await this.DbContext.Objects.AnyAsync(o => o.ObjectName == "alpha"), Is.True);
        }

        [Test]
        public async Task Delete_Owner_RemovesTheSecret()
        {
            await this.CreateSecretAsync("alpha");

            var result = await this.service.DeleteAsync(this.Owner, "alpha");

            Assert.That(result.IsOk, Is.True);
            Assert.That(await this.DbContext.Objects.AnyAsync(o => o.ObjectName == "alpha"), Is.False);
        }

        [Test]
        public async Task Lists_OnlyContainSecretsTheCallerCanRead()
        {
            await this.CreateSecretAsync("alpha");
            await this.CreateSecretAsync("beta");
            await this.GrantAsync("beta", ReaderUpn, PermissionType.Read);

            var v3 = await this.service.ListAsync(this.Reader, null);
            var v2 = await this.service.ListV2Async(this.Reader, null);
            var stranger = await this.service.ListAsync(this.Stranger, null);

            Assert.That(v3.Value!.Select(s => s.ObjectName), Is.EqualTo(new[] { "beta" }));
            Assert.That(v3.Value!.Single().CallerEffectivePermissions.CanRead, Is.True);
            Assert.That(v2.Value!.Select(s => s.ObjectName), Is.EqualTo(new[] { "beta" }));
            Assert.That(stranger.Value, Is.Empty);
        }

        [Test]
        public async Task List_InvalidTag_IsBadRequest()
        {
            var result = await this.service.ListAsync(this.Owner, new[] { "not a tag!" });

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.BadRequest));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(result.Error, Is.Not.Empty);
        }

        [Test]
        public void PurgeFailure_PropagatesUnwrapped()
        {
            var purger = new Mock<IPurger>();
            var failure = new InvalidOperationException("purge failed");
            purger.Setup(p => p.PurgeIfNeededAsync(It.IsAny<string>(), It.IsAny<SafeExchangeDbContext>(), It.IsAny<IAuditWriter>(), It.IsAny<int>())).ThrowsAsync(failure);
            var service = new SecretService(this.Configuration, this.DbContext, purger.Object, this.PermissionsManager, this.AuditWriter, NullLogger<SecretService>.Instance);

            var thrown = Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAsync(this.Owner, "alpha"));

            Assert.That(thrown, Is.SameAs(failure));
        }

        [Test]
        public async Task OperationFailure_IsWrappedInServiceOperationException()
        {
            await this.CreateSecretAsync("alpha");
            var permissions = new Mock<IPermissionsManager>();
            var failure = new InvalidOperationException("permission store failed");
            permissions.Setup(p => p.IsAuthorizedAsync(It.IsAny<SubjectType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PermissionType>())).ThrowsAsync(failure);
            var service = new SecretService(this.Configuration, this.DbContext, this.Purger, permissions.Object, this.AuditWriter, NullLogger<SecretService>.Instance);

            var thrown = Assert.ThrowsAsync<ServiceOperationException>(() => service.GetAsync(this.Owner, "alpha"));

            Assert.That(thrown!.InnerException, Is.SameAs(failure));
        }
    }
}
