/// <summary>
/// MetaCreationInput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    using System;
    using System.Collections.Generic;

    public class MetadataCreationInput
    {
        public ExpirationSettingsInput ExpirationSettings { get; set; }

        public List<string>? Tags { get; set; }

        public bool? AuditEnabled { get; set; }
    }
}
