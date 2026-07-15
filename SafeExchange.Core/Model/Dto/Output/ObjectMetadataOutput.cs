/// <summary>
/// ObjectMetadataOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;
    using System.Collections.Generic;

    public class ObjectMetadataOutput
    {
        public string ObjectName { get; set; }

        public List<ContentMetadataOutput> Content { get; set; }

        public ExpirationSettingsOutput ExpirationSettings { get; set; }

        public List<string> Tags { get; set; } = new();

        public bool AuditEnabled { get; set; }

        /// <summary>
        /// The current caller's effective permissions on this secret (direct grants unioned with
        /// group-derived grants). Additive field: null unless the endpoint populated it for a
        /// specific caller (the single-secret read path), so existing consumers are unaffected.
        /// </summary>
        public CallerPermissionsOutput? CallerPermissions { get; set; }
    }
}
