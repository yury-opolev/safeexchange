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
    using SafeExchange.Core.Utilities;
    using System;

    class GraphDataProvider : IGraphDataProvider
    {
        public const string ConsentRequiredSubStatus = "consent_required";

        private readonly static string[] MemberOfGraphScopes = [ "User.Read" ];

        private readonly static string[] UserSearchGraphScopes = [ "User.ReadBasic.All" ];

        private readonly static string[] GroupSearchGraphScopes = [ "GroupMember.Read.All" ];

        private readonly IConfidentialClientProvider aadClientProvider;

        private readonly ILogger<GraphDataProvider> log;

        public GraphDataProvider(IConfidentialClientProvider aadClientProvider, ILogger<GraphDataProvider> log)
        {
            this.aadClientProvider = aadClientProvider ?? throw new ArgumentNullException(nameof(aadClientProvider));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <inheritdoc />
        public async Task<GroupIdListResult> TryGetMemberOfAsync(AccountIdAndToken accountIdAndToken)
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
                    return new GroupIdListResult()
                    {
                        ConsentRequired = tokenResult.ConsentRequired,
                        ScopesToConsent = string.Join(' ', MemberOfGraphScopes)
                    };
                }

                var memberOf = await graphClient.Me.MemberOf.GetAsync(
                    requestConfiguration => requestConfiguration.QueryParameters.Select = [ "id" ]);

                if (memberOf == null)
                {
                    return new GroupIdListResult() { Success = true, GroupIds = totalGroups };
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
                return new GroupIdListResult();
            }

            return new GroupIdListResult() { Success = true, GroupIds = totalGroups };
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
                        requestConfiguration.QueryParameters.Search =
                            string.Join(" OR ",
                                $"\"displayName:{searchString}\"",
                                $"\"userPrincipalName:{searchString}\"",
                                $"\"mail:{searchString}\"");

                        requestConfiguration.QueryParameters.Select = [ "displayName, userPrincipalName" ];
                        requestConfiguration.QueryParameters.Top = totalUsers.Capacity;

                        requestConfiguration.QueryParameters.Count = true;
                        requestConfiguration.Headers.Add("ConsistencyLevel", "eventual");
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

        public async Task<GroupsListResult> TryFindGroupsAsync(AccountIdAndToken accountIdAndToken, string searchString)
        {
            var totalGroups = new List<GraphGroupInfo>(100);

            try
            {
                var aadClient = this.aadClientProvider.GetConfidentialClient();
                var accessTokenProvider = new OnBehalfOfAuthProvider(aadClient, accountIdAndToken, GroupSearchGraphScopes, this.log);
                var graphClient = new GraphServiceClient(new BaseBearerTokenAuthenticationProvider(accessTokenProvider));

                var tokenResult = await accessTokenProvider.TryGetAccessTokenAsync();
                if (!tokenResult.Success)
                {
                    return new GroupsListResult()
                    {
                        ConsentRequired = tokenResult.ConsentRequired,
                        ScopesToConsent = string.Join(' ', GroupSearchGraphScopes)
                    };
                }

                var foundGroups = await graphClient.Groups.GetAsync(
                    requestConfiguration =>
                    {
                        requestConfiguration.QueryParameters.Search =
                            string.Join(" OR ",
                                $"\"displayName:{searchString}\"",
                                $"\"mail:{searchString}\"");

                        requestConfiguration.QueryParameters.Select = ["id, displayName, mail"];
                        requestConfiguration.QueryParameters.Top = totalGroups.Capacity;

                        requestConfiguration.QueryParameters.Count = true;
                        requestConfiguration.Headers.Add("ConsistencyLevel", "eventual");
                    });

                if (foundGroups == null)
                {
                    return new GroupsListResult() { Success = true, Groups = totalGroups };
                }

                var pageIterator = PageIterator<Group, GroupCollectionResponse>
                    .CreatePageIterator(
                        graphClient,
                        foundGroups,
                        (group) =>
                        {
                            totalGroups.Add(new GraphGroupInfo(group.Id, group.DisplayName, group.Mail));
                            return true;
                        });

                await pageIterator.IterateAsync();
            }
            catch (Exception exception)
            {
                this.log.LogWarning($"Cannot retrieve groups, {TelemetryUtils.GetDescription(exception)}.");
                return new GroupsListResult();
            }

            return new GroupsListResult() { Success = true, Groups = totalGroups };
        }
    }
}
