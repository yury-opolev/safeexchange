/// <summary>
/// OnBehalfOfAuthProvider
/// </summary>

namespace SafeExchange.Core.Graph
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Graph;
    using Microsoft.Identity.Client;
    using System;
    using System.Net.Http.Headers;
    using System.Threading.Tasks;

    public class OnBehalfOfAuthProvider : IAuthenticationProvider
    {
        private IConfidentialClientApplication msalClient;

        private AccountIdAndToken accountIdAndToken;

        private string[] scopes;

        private ILogger logger;

        public OnBehalfOfAuthProvider(IConfidentialClientApplication msalClient, AccountIdAndToken accountIdAndToken, string[] scopes, ILogger logger)
        {
            this.scopes = scopes;
            this.logger = logger;

            this.accountIdAndToken = accountIdAndToken;
            this.msalClient = msalClient;
        }

        public async Task<string> GetAccessToken()
        {
            var account = await this.msalClient.GetAccountAsync(this.accountIdAndToken.AccountId);
            if (account != null)
            {
                return await this.GetTokenSilentlyAsync(account);
            }

            return await this.GetOnBehalfTokenAsync();
        }

        public async Task AuthenticateRequestAsync(HttpRequestMessage requestMessage)
        {
            var token = await GetAccessToken();
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        private async Task<string> GetTokenSilentlyAsync(IAccount account)
        {
            try
            {
                var cacheResult = await this.msalClient
                    .AcquireTokenSilent(this.scopes, account).ExecuteAsync();

                this.logger.LogInformation($"Token for [{string.Join(',', this.scopes)}] on behalf of '{this.accountIdAndToken.AccountId}' acquired silently.");
                return cacheResult.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                this.logger.LogInformation($"Cannot acquire token for [{string.Join(',', this.scopes)}] silently, UI required.");
            }
            catch (Exception exception)
            {
                this.logger.LogError(exception, $"{exception.GetType()} getting access token in {nameof(OnBehalfOfAuthProvider)}, silently.");
                return string.Empty;
            }

            return string.Empty;
        }

        private async Task<string> GetOnBehalfTokenAsync()
        {
            try
            {
                var userAssertion = new UserAssertion(this.accountIdAndToken.Token);

                var result = await this.msalClient
                    .AcquireTokenOnBehalfOf(this.scopes, userAssertion).ExecuteAsync();

                this.logger.LogInformation($"Acquired on-behalf of '{this.accountIdAndToken.AccountId}' access token for: [{string.Join(',', this.scopes)}]");
                return result.AccessToken;
            }
            catch (Exception exception)
            {
                this.logger.LogError(exception, $"{exception.GetType()} getting access token in {nameof(OnBehalfOfAuthProvider)}, on-behalf.");
                return string.Empty;
            }
        }
    }
}
