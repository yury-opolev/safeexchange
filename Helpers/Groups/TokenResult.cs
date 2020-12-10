/// SafeExchange

namespace SpaceOyster.SafeExchange
{
    public class TokenResult
    {
        public string AccountId { get; private set; }

        public string Token { get; private set; }

        public TokenResult(string accountId, string token)
        {
            this.AccountId = accountId;
            this.Token = token;
        }
    }
}