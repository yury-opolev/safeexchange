/// <summary>
/// GraphDataProvider
/// </summary>

namespace SafeExchange.Core.Graph
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Graph;
    using SafeExchange.Core.AzureAd;
    using System;

    class GraphDataProvider : IGraphDataProvider
    {
        private readonly static string[] GraphScopes = new string[] { "User.Read" };

        private readonly IConfidentialClientProvider aadClientProvider;

        private readonly ILogger<GraphDataProvider> log;

        public GraphDataProvider(IConfidentialClientProvider aadClientProvider, ILogger<GraphDataProvider> log)
        {
            this.aadClientProvider = aadClientProvider ?? throw new ArgumentNullException(nameof(aadClientProvider));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <inheritdoc />
        public async Task<GroupListResult> TryGetMemberOfAsync(AccountIdAndToken accountIdAndToken)
        {
            var totalGroups = new List<string>();

            try
            {
                var aadClient = this.aadClientProvider.GetConfidentialClient();
                var authProvider = new OnBehalfOfAuthProvider(aadClient, accountIdAndToken, GraphScopes, this.log);
                var graphClient = new GraphServiceClient(authProvider);

                var tokenResult = await authProvider.TryGetAccessTokenAsync();
                if (!tokenResult.Success)
                {
                    return new GroupListResult() { ConsentRequired = tokenResult.ConsentRequired };
                }

                var memberOf = await graphClient.Me.MemberOf.Request().Select("id").GetAsync();
                while (memberOf.Count > 0)
                {
                    foreach (Group group in memberOf)
                    {
                        totalGroups.Add(group.Id);
                    }

                    if (memberOf.NextPageRequest != null)
                    {
                        memberOf = await memberOf.NextPageRequest.GetAsync();
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                this.log.LogWarning($"Cannot retrieve user groups, {exception.GetType()}: {exception.Message}");
                return new GroupListResult();
            }

            return new GroupListResult() { Success = true, Groups = totalGroups };
        }
    }
}
