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
            Assert.AreEqual(PermissionType.None, permissions);

            permissionsInput = new SubjectPermissionsInput()
            {
                CanRead = true,
                CanWrite = true,
                CanGrantAccess = false,
                CanRevokeAccess = false
            };

            permissions = permissionsInput.GetPermissionType();
            Assert.AreEqual(PermissionType.Read | PermissionType.Write, permissions);

            permissionsInput = new SubjectPermissionsInput()
            {
                CanRead = true,
                CanWrite = true,
                CanGrantAccess = true,
                CanRevokeAccess = true
            };

            permissions = permissionsInput.GetPermissionType();
            Assert.AreEqual(PermissionType.Full, permissions);
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

            Assert.IsTrue(PermissionsManager.IsPresentPermission(fullPermissionSet, PermissionType.None));

            Assert.IsTrue(PermissionsManager.IsPresentPermission(fullPermissionSet, PermissionType.Read));
            Assert.IsTrue(PermissionsManager.IsPresentPermission(fullPermissionSet, PermissionType.Write));
            Assert.IsTrue(PermissionsManager.IsPresentPermission(fullPermissionSet, PermissionType.GrantAccess));
            Assert.IsTrue(PermissionsManager.IsPresentPermission(fullPermissionSet, PermissionType.RevokeAccess));

            Assert.IsTrue(PermissionsManager.IsPresentPermission(fullPermissionSet, PermissionType.Read | PermissionType.Write));
            Assert.IsTrue(PermissionsManager.IsPresentPermission(fullPermissionSet, PermissionType.GrantAccess | PermissionType.RevokeAccess));

            Assert.IsTrue(PermissionsManager.IsPresentPermission(fullPermissionSet, PermissionType.Full));
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

            Assert.IsTrue(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.None));

            Assert.IsFalse(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.Read));
            Assert.IsFalse(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.Write));
            Assert.IsFalse(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.GrantAccess));
            Assert.IsFalse(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.RevokeAccess));

            Assert.IsFalse(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.Read | PermissionType.Write));
            Assert.IsFalse(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.GrantAccess | PermissionType.RevokeAccess));

            Assert.IsFalse(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.Full));
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

            Assert.IsTrue(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.None));

            Assert.IsTrue(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.Read));
            Assert.IsTrue(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.Write));
            Assert.IsFalse(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.GrantAccess));
            Assert.IsFalse(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.RevokeAccess));

            Assert.IsTrue(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.Read | PermissionType.Write));
            Assert.IsFalse(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.GrantAccess | PermissionType.RevokeAccess));

            Assert.IsFalse(PermissionsManager.IsPresentPermission(emptyPermissionSet, PermissionType.Full));
        }
    }
}
