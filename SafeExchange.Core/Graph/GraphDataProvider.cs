/// <summary>
/// GraphDataProvider
/// </summary>

namespace SafeExchange.Core.Graph
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Graph;
    using Microsoft.Graph.Models;
    using Microsoft.Kiota.Abstractions.Authentication;
    using SafeExchange.Core.AzureAd;
    using System;
    using System.Text;

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
                var accessTokenProvider = new OnBehalfOfAuthProvider(aadClient, accountIdAndToken, GraphScopes, this.log);
                var graphClient = new GraphServiceClient(new BaseBearerTokenAuthenticationProvider(accessTokenProvider));

                var tokenResult = await accessTokenProvider.TryGetAccessTokenAsync();
                if (!tokenResult.Success)
                {
                    return new GroupListResult() { ConsentRequired = tokenResult.ConsentRequired };
                }

                var memberOf = await graphClient.Me.MemberOf.GetAsync(
                    requestConfiguration => requestConfiguration.QueryParameters.Select = new string[] { "id" });

                var pageIterator = PageIterator<DirectoryObject, DirectoryObjectCollectionResponse>
                    .CreatePageIterator(
                        graphClient,
                        memberOf,
                        (group) =>
                        {
                            totalGroups.Add(group.Id);
                            return true;
                        });

                await pageIterator.IterateAsync();
            }
            catch (Exception exception)
            {
                this.log.LogWarning($"Cannot retrieve user groups, {GetDescription(exception)}.");
                return new GroupListResult();
            }

            return new GroupListResult() { Success = true, Groups = totalGroups };
        }

        private string GetDescription(Exception exception)
        {
            var stringBuilder = new StringBuilder($"{exception.GetType()}: {exception.Message}");
            var currentException = exception.InnerException;
            while (currentException != null)
            {
                stringBuilder.Append($" -> {exception.GetType()}: {exception.Message}");
                currentException = currentException.InnerException;
            }

            return stringBuilder.ToString();
        }
    }
}
