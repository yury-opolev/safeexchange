/// <summary>
/// MetadataDiffBuilder — builds the SecretMetadataUpdated payload from the existing
/// ObjectMetadata and the incoming MetadataUpdateInput. Diff is restricted to
/// non-content fields. Returns null when there is no effective change.
/// </summary>

namespace SafeExchange.Core.Audit
{
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;

    public static class MetadataDiffBuilder
    {
        public static string? BuildDiff(ObjectMetadata existing, MetadataUpdateInput updated)
        {
            if (existing is null)
            {
                throw new ArgumentNullException(nameof(existing));
            }
            if (updated is null)
            {
                throw new ArgumentNullException(nameof(updated));
            }

            var changes = new Dictionary<string, object>();

            if (updated.Tags is not null)
            {
                var beforeTags = (existing.Tags ?? new List<string>()).OrderBy(t => t, StringComparer.Ordinal).ToList();
                var afterTags = updated.Tags.OrderBy(t => t, StringComparer.Ordinal).ToList();
                if (!beforeTags.SequenceEqual(afterTags))
                {
                    changes["tags"] = new
                    {
                        from = existing.Tags ?? new List<string>(),
                        to = updated.Tags,
                    };
                }
            }

            if (updated.ExpirationSettings is not null)
            {
                var beforeDto = existing.ExpirationMetadata.ToDto();
                if (!ExpirationSettingsEqual(beforeDto, updated.ExpirationSettings))
                {
                    changes["expirationSettings"] = new
                    {
                        from = beforeDto,
                        to = updated.ExpirationSettings,
                    };
                }
            }

            if (changes.Count == 0)
            {
                return null;
            }

            return DefaultJsonSerializer.Serialize(new { changes });
        }

        private static bool ExpirationSettingsEqual(Model.Dto.Output.ExpirationSettingsOutput a, ExpirationSettingsInput b)
        {
            return a.ScheduleExpiration == b.ScheduleExpiration
                && a.ExpireAt == b.ExpireAt
                && a.ExpireOnIdleTime == b.ExpireOnIdleTime
                && a.IdleTimeToExpire == b.IdleTimeToExpire;
        }
    }
}
