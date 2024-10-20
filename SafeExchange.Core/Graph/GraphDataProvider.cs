/// <summary>
/// GraphDataProvider
/// </summary>

namespace SafeExchange.Core.Graph
{
    using Azure.Identity;
    using Microsoft.Extensions.Logging;
    using Microsoft.Graph;
    using Microsoft.Graph.Models;
    using Microsoft.Kiota.Abstractions.Authentication;
    using SafeExchange.Core.AzureAd;
    using SafeExchange.Core.Utilities;
    using System;

    class GraphDataProvider : IGraphDataProvider
    {
        private readonly static string[] MemberOfGraphScopes = [ "User.Read" ];

        private readonly static string[] UserSearchGraphScopes = [ "User.ReadBasic.All" ];

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
                var accessTokenProvider = new OnBehalfOfAuthProvider(aadClient, accountIdAndToken, MemberOfGraphScopes, this.log);
                var graphClient = new GraphServiceClient(new BaseBearerTokenAuthenticationProvider(accessTokenProvider));

                var tokenResult = await accessTokenProvider.TryGetAccessTokenAsync();
                if (!tokenResult.Success)
                {
                    return new GroupListResult()
                    {
                        ConsentRequired = tokenResult.ConsentRequired,
                        ScopesToConsent = string.Join(' ', MemberOfGraphScopes)
                    };
                }

                var memberOf = await graphClient.Me.MemberOf.GetAsync(
                    requestConfiguration => requestConfiguration.QueryParameters.Select = [ "id" ]);

                if (memberOf == null)
                {
                    return new GroupListResult() { Success = true, Groups = totalGroups };
                }

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
                this.log.LogWarning($"Cannot retrieve user groups, {TelemetryUtils.GetDescription(exception)}.");
                return new GroupListResult();
            }

            return new GroupListResult() { Success = true, Groups = totalGroups };
        }

        public async Task<UsersListResult> TryFindUsersAsync(AccountIdAndToken accountIdAndToken, string searchString)
        {
            var totalUsers = new List<GraphUserInfo>(100);

            try
            {
                var aadClient = this.aadClientProvider.GetConfidentialClient();
                var accessTokenProvider = new OnBehalfOfAuthProvider(aadClient, accountIdAndToken, UserSearchGraphScopes, this.log);
                var graphClient = new GraphServiceClient(new BaseBearerTokenAuthenticationProvider(accessTokenProvider));

                var tokenResult = await accessTokenProvider.TryGetAccessTokenAsync();
                if (!tokenResult.Success)
                {
                    return new UsersListResult()
                    {
                        ConsentRequired = tokenResult.ConsentRequired,
                        ScopesToConsent = string.Join(' ', UserSearchGraphScopes)
                    };
                }

                var foundUsers = await graphClient.Users.GetAsync(
                    requestConfiguration =>
                    {
                        requestConfiguration.QueryParameters.Filter =
                            string.Join(" or ",
                                $"contains(displayName, '{searchString}')",
                                $"contains(userPrincipalName, '{searchString}')",
                                $"contains(mail, '{searchString}')");

                        requestConfiguration.QueryParameters.Select = [ "displayName, userPrincipalName" ];
                        requestConfiguration.QueryParameters.Top = totalUsers.Capacity;
                    });

                if (foundUsers == null)
                {
                    return new UsersListResult() { Success = true, Users = totalUsers };
                }

                var pageIterator = PageIterator<User, UserCollectionResponse>
                    .CreatePageIterator(
                        graphClient,
                        foundUsers,
                        (user) =>
                        {
                            totalUsers.Add(new GraphUserInfo(user.DisplayName, user.UserPrincipalName));
                            return true;
                        });

                await pageIterator.IterateAsync();
            }
            catch (Exception exception)
            {
                this.log.LogWarning($"Cannot retrieve users, {TelemetryUtils.GetDescription(exception)}.");
                return new UsersListResult();
            }

            return new UsersListResult() { Success = true, Users = totalUsers };
        }
    }
}
