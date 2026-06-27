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
        public void ComputeNetPermission_AllCombinations_MatchPerBitSpecification()
        {
            var bits = new[]
            {
                PermissionType.Read,
                PermissionType.Write,
                PermissionType.GrantAccess,
                PermissionType.RevokeAccess
            };

            // Exhaustively cover every (existing, remove, add) flag combination — 16 x 16 x 16 = 4096.
            for (var e = 0; e < 16; e++)
            {
                for (var r = 0; r < 16; r++)
                {
                    for (var a = 0; a < 16; a++)
                    {
                        var existing = (PermissionType)e;
                        var remove = (PermissionType)r;
                        var add = (PermissionType)a;

                        var net = PermissionsManager.ComputeNetPermission(existing, remove, add);

                        foreach (var bit in bits)
                        {
                            var hasExisting = (existing & bit) == bit;
                            var hasRemove = (remove & bit) == bit;
                            var hasAdd = (add & bit) == bit;

                            // Independent per-bit specification (not the formula under test):
                            // a flag is present iff it was held and not removed, or it was added.
                            var expected = (hasExisting && !hasRemove) || hasAdd;
                            var actual = (net & bit) == bit;

                            Assert.That(actual, Is.EqualTo(expected),
                                $"bit {bit}: existing={existing}, remove={remove}, add={add} -> net={net}");
                        }
                    }
                }
            }
        }

        [Test]
        public void ComputeNetPermission_RemoveThenReAddSameSubject_BroadensInsteadOfVanishing()
        {
            // The production regression: a subject with Read is removed (Read) and re-added (Read+Write)
            // in one atomic request. The net must be Read+Write — the row must NOT collapse to None.
            var net = PermissionsManager.ComputeNetPermission(
                PermissionType.Read,
                PermissionType.Read,
                PermissionType.Read | PermissionType.Write);

            Assert.That(net, Is.EqualTo(PermissionType.Read | PermissionType.Write));
        }

        [Test]
        public void ComputeNetPermission_RemoveOnly_CollapsesToNone()
        {
            // Pure removal with no re-add nets to None, signalling the row should be deleted.
            var net = PermissionsManager.ComputeNetPermission(
                PermissionType.Read | PermissionType.Write,
                PermissionType.Read | PermissionType.Write,
                PermissionType.None);

            Assert.That(net, Is.EqualTo(PermissionType.None));
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
