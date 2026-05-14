/// <summary>
/// MetadataDiffBuilderTests
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using System;
    using System.Collections.Generic;
    using System.Text.Json;

    [TestFixture]
    public class MetadataDiffBuilderTests
    {
        private static ExpirationSettingsInput NewExpiration(int days)
            => new()
            {
                ScheduleExpiration = true,
                ExpireAt = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(days),
                ExpireOnIdleTime = false,
                IdleTimeToExpire = TimeSpan.Zero,
            };

        private static ObjectMetadata NewExisting(List<string> tags, ExpirationSettingsInput? expirationInput = null)
        {
            var input = new MetadataCreationInput
            {
                Tags = tags,
                ExpirationSettings = expirationInput ?? NewExpiration(1),
            };
            return new ObjectMetadata("x", input, "User u@x");
        }

        [Test]
        public void BuildDiff_NoChange_ReturnsNull()
        {
            var existing = NewExisting(new List<string> { "a", "b" });
            var updated = new MetadataUpdateInput
            {
                Tags = new List<string> { "b", "a" },
                ExpirationSettings = NewExpiration(1),
            };
            Assert.That(MetadataDiffBuilder.BuildDiff(existing, updated), Is.Null);
        }

        [Test]
        public void BuildDiff_TagsAdded_IncludesFromAndTo()
        {
            var existing = NewExisting(new List<string> { "a" });
            var updated = new MetadataUpdateInput
            {
                Tags = new List<string> { "a", "b" },
                ExpirationSettings = NewExpiration(1),
            };
            var diff = MetadataDiffBuilder.BuildDiff(existing, updated);
            Assert.That(diff, Is.Not.Null);
            using var doc = JsonDocument.Parse(diff!);
            var tagsChange = doc.RootElement.GetProperty("changes").GetProperty("tags");
            Assert.That(tagsChange.GetProperty("from").GetArrayLength(), Is.EqualTo(1));
            Assert.That(tagsChange.GetProperty("to").GetArrayLength(), Is.EqualTo(2));
        }

        [Test]
        public void BuildDiff_TagsRemoved_IncludesFromAndTo()
        {
            var existing = NewExisting(new List<string> { "a", "b" });
            var updated = new MetadataUpdateInput
            {
                Tags = new List<string> { "a" },
                ExpirationSettings = NewExpiration(1),
            };
            var diff = MetadataDiffBuilder.BuildDiff(existing, updated);
            Assert.That(diff, Is.Not.Null);
            using var doc = JsonDocument.Parse(diff!);
            var tagsChange = doc.RootElement.GetProperty("changes").GetProperty("tags");
            Assert.That(tagsChange.GetProperty("from").GetArrayLength(), Is.EqualTo(2));
            Assert.That(tagsChange.GetProperty("to").GetArrayLength(), Is.EqualTo(1));
        }

        [Test]
        public void BuildDiff_ExpirationChanged_IncludesFromAndTo()
        {
            var existing = NewExisting(new List<string>(), NewExpiration(1));
            var updated = new MetadataUpdateInput
            {
                Tags = null,
                ExpirationSettings = NewExpiration(2),
            };
            var diff = MetadataDiffBuilder.BuildDiff(existing, updated);
            Assert.That(diff, Is.Not.Null);
            using var doc = JsonDocument.Parse(diff!);
            Assert.That(doc.RootElement.GetProperty("changes").TryGetProperty("expirationSettings", out _), Is.True);
        }

        [Test]
        public void BuildDiff_TagsNullInUpdate_TreatedAsNoChangeToTags()
        {
            var existing = NewExisting(new List<string> { "a" });
            var updated = new MetadataUpdateInput
            {
                Tags = null,
                ExpirationSettings = NewExpiration(1),
            };
            Assert.That(MetadataDiffBuilder.BuildDiff(existing, updated), Is.Null);
        }

        [Test]
        public void BuildDiff_BothChanged_IncludesBoth()
        {
            var existing = NewExisting(new List<string> { "a" }, NewExpiration(1));
            var updated = new MetadataUpdateInput
            {
                Tags = new List<string> { "a", "b" },
                ExpirationSettings = NewExpiration(2),
            };
            var diff = MetadataDiffBuilder.BuildDiff(existing, updated);
            Assert.That(diff, Is.Not.Null);
            using var doc = JsonDocument.Parse(diff!);
            var changes = doc.RootElement.GetProperty("changes");
            Assert.That(changes.TryGetProperty("tags", out _), Is.True);
            Assert.That(changes.TryGetProperty("expirationSettings", out _), Is.True);
        }
    }
}
