/// <summary>
/// PermissionsManagerAndInputTests
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;

    [TestFixture]
    public class PermissionsManagerAndInputTests
    {
        [Test]
        public void PermissionsInputSet()
        {
            var permissionsInput = new SubjectPermissionsInput()
            {
                CanRead = false,
                CanWrite = false,
                CanGrantAccess = false,
                CanRevokeAccess = false
            };

            var permissions = permissionsInput.GetPermissionType();
            Assert.That(permissions, Is.EqualTo(PermissionType.None));

            permissionsInput = new SubjectPermissionsInput()
            {
                CanRead = true,
                CanWrite = true,
                CanGrantAccess = false,
                CanRevokeAccess = false
            };

            permissions = permissionsInput.GetPermissionType();
            Assert.That(permissions, Is.EqualTo(PermissionType.Read | PermissionType.Write));

            permissionsInput = new SubjectPermissionsInput()
            {
                CanRead = true,
                CanWrite = true,
                CanGrantAccess = true,
                CanRevokeAccess = true
            };

            permissions = permissionsInput.GetPermissionType();
            Assert.That(permissions, Is.EqualTo(PermissionType.Full));
        }

        [Test]
        public void FullPermissionSet()
        {
            var fullPermissionSet = new SubjectPermissions()
            {
                CanRead = true,
                CanWrite = true,
                CanGrantAccess = true,
                CanRevokeAccess = true
            };

            Assert.That(PermissionsManager.IsPresentPermission(fullPermissionSet, PermissionType.None), Is.True);

            Assert.That(PermissionsManager.IsPresentPermission(fullPermissionSet, PermissionType.Read), Is.True);
            Assert.That(PermissionsManager.IsPresentPermission(fullPermissionSet, PermissionType.Write), Is.True);
            Assert.That(PermissionsManager.IsPresentPermission(fullPermissionSet, PermissionType.GrantAccess), Is.True);
            Assert.That(PermissionsManager.IsPresentPermission(fullPermissionSet, PermissionType.RevokeAccess), Is.True);

            Assert.That(PermissionsManager.IsPresentPermission(fullPermissionSet, PermissionType.Read | PermissionType.Write), Is.True);
            Assert.That(PermissionsManager.IsPresentPermission(fullPermissionSet, PermissionType.GrantAccess | PermissionType.RevokeAccess), Is.True);

            Assert.That(PermissionsManager.IsPresentPermission(fullPermissionSet, PermissionType.Full), Is.True);
        }

        [Test]
        public void EmptyPermissionSet()
        {
            var emptyPermissionSet = new SubjectPermissions()
            {
                CanRead = false,
                CanWrite = false,
                CanGrantAccess = false,
                CanRevokeAccess = false
            };

            Assert.That(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.None), Is.True);

            Assert.That(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.Read), Is.False);
            Assert.That(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.Write), Is.False);
            Assert.That(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.GrantAccess), Is.False);
            Assert.That(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.RevokeAccess), Is.False);

            Assert.That(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.Read | PermissionType.Write), Is.False);
            Assert.That(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.GrantAccess | PermissionType.RevokeAccess), Is.False);

            Assert.That(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.Full), Is.False);
        }

        [Test]
        public void PartialPermissionSet()
        {
            var emptyPermissionSet = new SubjectPermissions()
            {
                CanRead = true,
                CanWrite = true,
                CanGrantAccess = false,
                CanRevokeAccess = false
            };

            Assert.That(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.None), Is.True);

            Assert.That(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.Read), Is.True);
            Assert.That(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.Write), Is.True);
            Assert.That(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.GrantAccess), Is.False);
            Assert.That(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.RevokeAccess), Is.False);

            Assert.That(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.Read | PermissionType.Write), Is.True);
            Assert.That(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.GrantAccess | PermissionType.RevokeAccess), Is.False);

            Assert.That(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.Full), Is.False);
        }
    }
}
