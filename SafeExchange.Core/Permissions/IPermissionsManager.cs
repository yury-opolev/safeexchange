/// <summary>
/// IPermissionsManager
/// </summary>

namespace SafeExchange.Core.Permissions
{
    using System.Threading.Tasks;
    using SafeExchange.Core.Model;

    public interface IPermissionsManager
    {
        /// <summary>
        /// Return true if specified user is required to consent to the AAD application in order to use groups authorization.
        /// </summary>
        /// <param name="userId">Specified user id.</param>
        /// <returns>Task, representing asynchronous operation.</returns>
        public Task<bool> IsConsentRequiredAsync(string userId);

        /// <summary>
        /// Return true if specified user has specified permission to specified secret, return false otherwise.
        /// </summary>
        /// <param name="subjectType">Specified subject type.</param>
        /// <param name="subjectId">Specified subject id.</param>
        /// <param name="secretId">Specified secret id.</param>
        /// <param name="permission">Specified permisiion.</param>
        /// <returns>Task, representing asynchronous operation.</returns>
        public Task<bool> IsAuthorizedAsync(SubjectType subjectType, string subjectId, string secretId, PermissionType permission);

        /// <summary>
        /// Add permission for specified subject to access specified secret. This does not commit changes, need to save explicitly afterwards.
        /// </summary>
        /// <param name="subjectType">Specified subject type.</param>
        /// <param name="subjectId">Specified user id.</param>
        /// <param name="secretId">Specified secret id.</param>
        /// <param name="permission">Specified permisiion.</param>
        /// <returns>Task, representing asynchronous operation.</returns>
        [Obsolete("This method is obsolete. Call SetPermissionAsync with (subjectId + subjectName) parameters instead.", true)]
        public Task SetPermissionAsync(SubjectType subjectType, string subjectId, string secretId, PermissionType permission);

        /// <summary>
        /// Add permission for specified subject to access specified secret. This does not commit changes, need to save explicitly afterwards.
        /// </summary>
        /// <param name="subjectType">Specified subject type.</param>
        /// <param name="subjectId">Specified subject id.</param>
        /// <param name="subjectName">Specified subject name.</param>
        /// <param name="secretId">Specified secret id.</param>
        /// <param name="permission">Specified permisiion.</param>
        /// <returns>Task, representing asynchronous operation.</returns>
        public Task SetPermissionAsync(SubjectType subjectType, string subjectId, string subjectName, string secretId, PermissionType permission);

        /// <summary>
        /// Applies an atomic net permission change for a single subject on a secret: computes
        /// '(existing &amp; ~removeFlags) | addFlags' and writes the result exactly once — creating,
        /// updating or deleting the permission row as needed. Coalesces a same-request remove+add
        /// for the same subject into one write so the row is never deleted-then-re-inserted within a
        /// single unit of work. Does not commit changes, save explicitly afterwards.
        /// </summary>
        /// <param name="subjectType">Specified subject type.</param>
        /// <param name="subjectId">Specified subject id.</param>
        /// <param name="subjectName">Specified subject name.</param>
        /// <param name="secretId">Specified secret id.</param>
        /// <param name="removeFlags">Permission flags to remove.</param>
        /// <param name="addFlags">Permission flags to add.</param>
        /// <returns>The permission set before and after the change.</returns>
        public Task<(PermissionType before, PermissionType after)> ApplyNetPermissionAsync(
            SubjectType subjectType, string subjectId, string subjectName, string secretId,
            PermissionType removeFlags, PermissionType addFlags);

        /// <summary>
        /// remove permission for specified user to access specified secret. This does not commit changes, need to save explicitly afterwards.
        /// </summary>
        /// <param name="subjectType">Specified subject type.</param>
        /// <param name="subjectId">Specified subject id.</param>
        /// <param name="secretId">Specified secret id.</param>
        /// <param name="permission">Specified permisiion.</param>
        /// <returns>Task, representing asynchronous operation.</returns>
        public Task UnsetPermissionAsync(SubjectType subjectType, string subjectId, string secretId, PermissionType permission);

        /// <summary>
        /// Returns true if the specified subject has at least one permission flag
        /// (Read, Write, GrantAccess, or RevokeAccess) on the specified secret,
        /// either directly or via group membership.
        /// </summary>
        /// <param name="subjectType">Specified subject type.</param>
        /// <param name="subjectId">Specified subject id.</param>
        /// <param name="secretId">Specified secret id.</param>
        /// <returns>Task, representing asynchronous operation.</returns>
        public Task<bool> HasAnyAccessAsync(SubjectType subjectType, string subjectId, string secretId);

        /// <summary>
        /// Returns the direct permission row for the specified subject on the specified secret,
        /// or null if no direct row exists.
        /// </summary>
        /// <param name="secretName">Specified secret name.</param>
        /// <param name="subjectType">Specified subject type.</param>
        /// <param name="subjectId">Specified subject id.</param>
        /// <returns>Task, representing asynchronous operation.</returns>
        public Task<SubjectPermissions?> GetSubjectPermissionsAsync(string secretName, SubjectType subjectType, string subjectId);
    }
}
