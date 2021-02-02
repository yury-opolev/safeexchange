/// <summary>
/// SafeExchange
/// </summary>

namespace SafeExchange.Core.Helpers.CosmosDb
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    public class DatabaseAccountListKeysResult
    {
        public string primaryMasterKey { get; set; }

        public string primaryReadonlyMasterKey { get; set; }

        public string secondaryMasterKey { get; set; }

        public string secondaryReadonlyMasterKey { get; set; }
    }
}
