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
    }
}
