/// <summary>
/// S2SAllowedTenant — a single entry in the Authentication:S2SAllowedTenants
/// allowlist, plus the parser/lookup helpers. Parsing FAILS CLOSED: any malformed
/// or missing configuration yields an empty list (no S2S tenants trusted) rather
/// than throwing — a bad config value must not widen trust, and must not take the
/// whole Functions app down on startup.
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Generic;
    using System.Text.Json;

    public sealed class S2SAllowedTenant
    {
        public S2SAllowedTenant(string tenantId, string displayName)
        {
            this.TenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            this.DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        }

        /// <summary>Entra tenant id (GUID) whose app-only tokens are accepted.</summary>
        public string TenantId { get; }

        /// <summary>Friendly label shown in the registration tenant picker.</summary>
        public string DisplayName { get; }

        /// <summary>
        /// Parses the JSON-array config value into a validated, de-duplicated list.
        /// Non-GUID tenant ids and non-object entries are skipped; a missing
        /// displayName defaults to the tenant id; duplicate tenant ids keep the
        /// first occurrence. Returns an empty list for null/empty/malformed input.
        /// </summary>
        public static IReadOnlyList<S2SAllowedTenant> ParseList(string? configValue, ILogger? log = null)
        {
            var result = new List<S2SAllowedTenant>();
            if (string.IsNullOrWhiteSpace(configValue))
            {
                return result;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(configValue);
            }
            catch (JsonException ex)
            {
                log?.LogError(ex, "Authentication:S2SAllowedTenants is not valid JSON; treating as empty (no S2S tenants trusted).");
                return result;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    log?.LogError("Authentication:S2SAllowedTenants must be a JSON array; treating as empty (no S2S tenants trusted).");
                    return result;
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var tenantId = GetString(element, "tenantId")?.Trim();
                    if (string.IsNullOrWhiteSpace(tenantId) || !Guid.TryParseExact(tenantId, "D", out _))
                    {
                        log?.LogWarning("Skipping S2S allowed tenant entry with missing or non-GUID tenantId.");
                        continue;
                    }

                    if (!seen.Add(tenantId))
                    {
                        continue;
                    }

                    var displayName = GetString(element, "displayName")?.Trim();
                    result.Add(new S2SAllowedTenant(
                        tenantId,
                        string.IsNullOrWhiteSpace(displayName) ? tenantId : displayName));
                }
            }

            return result;
        }

        /// <summary>Case-insensitive membership test by tenant id.</summary>
        public static bool Contains(IReadOnlyList<S2SAllowedTenant> tenants, string? tenantId)
        {
            if (tenants is null || string.IsNullOrWhiteSpace(tenantId))
            {
                return false;
            }

            foreach (var tenant in tenants)
            {
                if (string.Equals(tenant.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string? GetString(JsonElement element, string propertyName)
            => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
