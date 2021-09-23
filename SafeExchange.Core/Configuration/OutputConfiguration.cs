/// <summary>
/// ...
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using System;

    public class OutputConfiguration
    {
        public ConfigurationData ConfigurationData { get; set; }

        public VapidOptions VapidOptions { get; set; }

        public OutputConfiguration()
        {
            //...
        }

        public OutputConfiguration(ConfigurationData configurationData, VapidOptions vapidOptions)
        {
            this.ConfigurationData = configurationData;
            this.VapidOptions = vapidOptions;
        }
    }
}
