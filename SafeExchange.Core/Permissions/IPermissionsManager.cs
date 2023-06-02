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
        public Task SetPermissionAsync(SubjectType subjectType, string subjectId, string secretId, PermissionType permission);

        /// <summary>
        /// remove permission for specified user to access specified secret. This does not commit changes, need to save explicitly afterwards.
        /// </summary>
        /// <param name="subjectType">Specified subject type.</param>
        /// <param name="subjectId">Specified subject id.</param>
        /// <param name="secretId">Specified secret id.</param>
        /// <param name="permission">Specified permisiion.</param>
        /// <returns>Task, representing asynchronous operation.</returns>
        public Task UnsetPermissionAsync(SubjectType subjectType, string subjectId, string secretId, PermissionType permission);
    }
}
