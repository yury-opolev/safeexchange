/// <summary>
/// DatabaseAccountListKeysResult
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using System;

    public class DatabaseAccountListKeysResult
    {
        public string primaryMasterKey { get; set; }

        public string primaryReadonlyMasterKey { get; set; }

        public string secondaryMasterKey { get; set; }

        public string secondaryReadonlyMasterKey { get; set; }
    }
}
