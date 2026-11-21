/// <summary>
/// AccessServiceTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Groups;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    [TestFixture]
    public class AccessServiceTests : ServiceTestBase
    {
        private AccessService service = null!;

        [SetUp]
        public void SetUp()
        {
            var features = new Features();
            var orphanedSecretConfiguration = new OrphanedSecretConfiguration();
            var orphanedSecretManager = new OrphanedSecretManager(
                Mock.Of<IOptionsMonitor<Features>>(x => x.CurrentValue == features),
                Mock.Of<IOptionsMonitor<OrphanedSecretConfiguration>>(x => x.CurrentValue == orphanedSecretConfiguration),
                NullLogger<OrphanedSecretManager>.Instance);
            this.service = new AccessService(
                this.DbContext, new GroupsManager(this.DbContext, NullLogger<GroupsManager>.Instance), this.PermissionsManager,
                this.AuditWriter, orphanedSecretManager, NullLogger<AccessService>.Instance);
        }

        [Test]
        public async Task Grant_ThenList_ShowsTheNewSubject()
        {
            await this.CreateSecretAsync("alpha");

            var grant = await this.service.GrantAsync(this.Owner, "alpha", ReaderCanRead());
            var list = await this.service.ListAsync(this.Owner, "alpha");
            var listV2 = await this.service.ListV2Async(this.Owner, "alpha");

            Assert.That(grant.IsOk, Is.True);
            Assert.That(list.Value!.AccessList.Select(p => p.SubjectName), Is.EquivalentTo(new[] { OwnerUpn, ReaderUpn }));
            Assert.That(list.Value!.CallerEffectivePermissions.CanRevokeAccess, Is.True);
            Assert.That(listV2.Value!.Select(p => p.SubjectName), Is.EquivalentTo(new[] { OwnerUpn, ReaderUpn }));
        }

        [Test]
        public async Task EveryMethod_DeniesACallerWithoutThePermission()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);
            var add = new AccessUpdateInput { Add = ReaderCanRead() };
            var remove = new AccessUpdateInput { Remove = ReaderCanRead() };

            AssertInsufficientPermissions(await this.service.GrantAsync(this.Reader, "alpha", ReaderCanRead()), PermissionType.GrantAccess, "alpha");
            AssertInsufficientPermissions(await this.service.RevokeAsync(this.Reader, "alpha", ReaderCanRead()), PermissionType.RevokeAccess, "alpha");
            AssertInsufficientPermissions(await this.service.PatchAsync(this.Reader, "alpha", add), PermissionType.GrantAccess, "alpha");
            AssertInsufficientPermissions(await this.service.PatchAsync(this.Reader, "alpha", remove), PermissionType.RevokeAccess, "alpha");
            AssertInsufficientPermissions((await this.service.ListAsync(this.Stranger, "alpha")).Outcome, PermissionType.Read, "alpha");
            AssertInsufficientPermissions((await this.service.ListV2Async(this.Stranger, "alpha")).Outcome, PermissionType.Read, "alpha");
        }

        [Test]
        public async Task Denial_ForAUserWhoMustConsent_CarriesTheConsentSubStatus()
        {
            await this.CreateSecretAsync("alpha");
            this.DbContext.Users.Add(new User("Stranger", "00000000-0000-0000-0000-0000000000a3", "00000000-0000-0000-0000-000000000001", StrangerUpn, StrangerUpn) { ConsentRequired = true });
            await this.DbContext.SaveChangesAsync();

            var result = await this.service.GrantAsync(this.Stranger, "alpha", ReaderCanRead());

            AssertInsufficientPermissions(result, PermissionType.GrantAccess, "alpha", "consent_required");
        }

        [Test]
        public async Task MissingSecret_IsNotFound_WithTheAccessWording()
        {
            var result = await this.service.ListAsync(this.Owner, "missing");

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.NotFound));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(result.Error, Is.EqualTo("Secret 'missing' not exists"));
        }

        [Test]
        public async Task EmptyInput_IsAnErrorStatusWith400()
        {
            await this.CreateSecretAsync("alpha");

            var grant = await this.service.GrantAsync(this.Owner, "alpha", null);
            var patch = await this.service.PatchAsync(this.Owner, "alpha", new AccessUpdateInput());

            Assert.That(grant.Status, Is.EqualTo(ServiceStatus.Error));
            Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(grant.Error, Is.EqualTo("Access settings are not provided."));
            Assert.That(patch.Status, Is.EqualTo(ServiceStatus.Error));
            Assert.That(patch.Error, Is.EqualTo("Access update body must include at least one of 'add' or 'remove'."));
        }

        [Test]
        public void LookupFailure_PropagatesUnwrapped_ButWorkFailureIsWrapped()
        {
            var permissions = new Mock<IPermissionsManager>();
            var authorizationFailure = new InvalidOperationException("authorization store failed");
            permissions.Setup(p => p.IsAuthorizedAsync(It.IsAny<SubjectType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PermissionType>())).ThrowsAsync(authorizationFailure);
            this.DbContext.Objects.Add(new ObjectMetadata("alpha", CreationInput(), "User owner"));
            this.DbContext.SaveChanges();
            var service = new AccessService(
                this.DbContext, Mock.Of<IGroupsManager>(), permissions.Object, this.AuditWriter, Mock.Of<IOrphanedSecretManager>(), NullLogger<AccessService>.Instance);

            var thrown = Assert.ThrowsAsync<InvalidOperationException>(() => service.GrantAsync(this.Owner, "alpha", ReaderCanRead()));
            var wrapped = Assert.ThrowsAsync<ServiceOperationException>(() => service.PatchAsync(this.Owner, "alpha", new AccessUpdateInput { Add = ReaderCanRead() }));

            Assert.That(thrown, Is.SameAs(authorizationFailure));
            Assert.That(wrapped!.InnerException, Is.SameAs(authorizationFailure));
        }

        private static List<SubjectPermissionsInput> ReaderCanRead()
            => new() { new SubjectPermissionsInput { SubjectType = SubjectTypeInput.User, SubjectName = ReaderUpn, CanRead = true } };
    }
}
