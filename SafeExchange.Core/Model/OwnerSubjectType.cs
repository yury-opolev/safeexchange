/// <summary>
/// OwnerSubjectType — the kind of principal that owns an Application.
/// </summary>

namespace SafeExchange.Core.Model
{
    public enum OwnerSubjectType
    {
        /// <summary>Owner is a human user (identified by UPN).</summary>
        User = 0,

        /// <summary>Owner is a group; any member of the group inherits ownership at request time.</summary>
        Group = 1,
    }
}
