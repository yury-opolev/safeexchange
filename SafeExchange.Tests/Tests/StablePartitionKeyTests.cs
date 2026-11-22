/// <summary>
/// Partition keys must be identical across processes/instances; values below were
/// computed independently (SHA-256, first 4 bytes big-endian, mod 10000).
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Utilities;
    using System;

    [TestFixture]
    public class StablePartitionKeyTests
    {
        [TestCase("secret-one", "3788")]
        [TestCase("my-secret", "5486")]
        [TestCase("Ünïcode-секрет", "7056")]
        public void ObjectMetadata_PartitionKey_IsStable(string name, string expected)
        {
            var input = new MetadataCreationInput
            {
                ExpirationSettings = new ExpirationSettingsInput
                {
                    ScheduleExpiration = false,
                    ExpireAt = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                },
            };

            Assert.That(new ObjectMetadata(name, input, "User u@x").PartitionKey, Is.EqualTo(expected));
        }

        [TestCase("secret-one", "3788")]
        [TestCase("my-secret", "5486")]
        public void SubjectPermissions_PartitionKey_IsStable(string secretName, string expected)
        {
            var permissions = new SubjectPermissions(secretName, SubjectType.User, "u@x");

            Assert.That(permissions.PartitionKey, Is.EqualTo(expected));
        }

        [TestCase("00000000-0000-0000-0000-000000000001", "6119")]
        [TestCase("a1b2c3d4-aaaa-bbbb-cccc-1234567890ab", "9623")]
        public void GroupDictionaryItem_PartitionKey_IsStable(string groupId, string expected)
        {
            var item = new GroupDictionaryItem(groupId, "Group", "group@x", "User u@x");

            Assert.That(item.PartitionKey, Is.EqualTo(expected));
        }

        [TestCase("")]
        [TestCase("secret-one")]
        [TestCase("日本語シークレット")]
        public void From_ReturnsFourAsciiDigits(string value)
        {
            Assert.That(StablePartitionKey.From(value), Does.Match("^[0-9]{4}$"));
        }

        [Test]
        public void From_LongInput_MatchesIndependentValue()
        {
            Assert.That(StablePartitionKey.From(new string('x', 5000)), Is.EqualTo("6068"));
        }

        [Test]
        public void From_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => StablePartitionKey.From(null!));
        }

        [Test]
        public void AccessRequest_PartitionKey_IsDerivedFromIdWithStableHash()
        {
            var request = new AccessRequest("secret-one", SubjectType.User, "u@x", new SubjectPermissionsInput { CanRead = true });

            // Id is a random GUID; the expected key is computed independently here.
            var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.Id));
            var expected = (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(digest) % 10000).ToString("D4");

            Assert.That(request.PartitionKey, Is.EqualTo(expected));
        }
    }
}
