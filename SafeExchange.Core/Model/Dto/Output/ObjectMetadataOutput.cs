/// <summary>
/// ObjectMetadataOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

    public class ObjectMetadataOutput
    {
        public string ObjectName { get; set; }

        public List<ContentMetadataOutput> Content { get; set; }

        public ExpirationSettingsOutput ExpirationSettings { get; set; }
    }
}
