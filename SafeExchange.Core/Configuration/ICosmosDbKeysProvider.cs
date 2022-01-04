/// <summary>
/// ICosmosDbKeysProvider
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using System;

    public interface ICosmosDbKeysProvider
    {
        public ValueTask<string> GetPrimaryKeyAsync();
    }
}
