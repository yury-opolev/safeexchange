/// <summary>
/// PermissionNetChange
/// </summary>

namespace SafeExchange.Core.Functions
{
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Permissions;

    /// <summary>
    /// Accumulates the net permission delta for a single resolved subject while an atomic access
    /// PATCH is coalesced: all 'remove' flags OR-ed together and all 'add' flags OR-ed together,
    /// applied once at the end as '(existing &amp; ~Remove) | Add'.
    /// </summary>
    internal sealed class PermissionNetChange
    {
        public PermissionNetChange(SubjectType subjectType, string subjectId, string subjectName)
        {
            this.SubjectType = subjectType;
            this.SubjectId = subjectId;
            this.SubjectName = subjectName;
        }

        public SubjectType SubjectType { get; }

        public string SubjectId { get; }

        public string SubjectName { get; }

        public PermissionType Remove { get; set; }

        public PermissionType Add { get; set; }
    }
}
