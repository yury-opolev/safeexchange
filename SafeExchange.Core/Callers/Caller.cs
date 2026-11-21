/// <summary>
/// Caller
/// </summary>

namespace SafeExchange.Core.Callers
{
    using SafeExchange.Core.Model;
    using System.Collections.Generic;

    /// <summary>
    /// The authenticated subject a service call is made for.
    /// </summary>
    /// <param name="SubjectType">User or Application.</param>
    /// <param name="SubjectId">The id permissions and audit use: the UPN for users, the registered
    /// application's display name for applications (as <see cref="SubjectHelper"/> resolves it).</param>
    /// <param name="DisplayName">The token's display name for users (the UPN when the token has none),
    /// the registered display name for applications.</param>
    /// <param name="GroupIds">Group ids of the caller, when a resolver knows them. The default
    /// <see cref="CallerResolver"/> leaves this empty: permission checks read group memberships from the
    /// Users row themselves (<c>IPermissionsManager</c>), so the resolver adds no lookup for them.</param>
    /// <param name="Channel">Where the call came from, one of <see cref="CallerChannels"/>.</param>
    /// <param name="ClientLabel">Self-reported client label (MCP only); carries no authority.</param>
    public sealed record Caller(
        SubjectType SubjectType,
        string SubjectId,
        string DisplayName,
        IReadOnlyList<string> GroupIds,
        string Channel,
        string? ClientLabel);
}
