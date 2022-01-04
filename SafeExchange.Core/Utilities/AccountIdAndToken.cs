/// <summary>
/// AccountIdAndToken
/// </summary>

namespace SafeExchange.Core
{
    using System;

    public class AccountIdAndToken
    {
        public string AccountId { get; private set; }

        public string Token { get; private set; }

        public AccountIdAndToken(string accountId, string token)
        {
            this.AccountId = accountId;
            this.Token = token;
        }
    }
}
