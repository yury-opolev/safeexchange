/// <summary>
/// IPermissionsManager
/// </summary>

namespace SafeExchange.Core.Permissions
{
    using System.Threading.Tasks;

    public interface IPermissionsManager
    {
        /// <summary>
        /// Return true if specified user has specified permission to specified secret, return false otherwise.
        /// </summary>
        /// <param name="userId">Specified user id.</param>
        /// <param name="secretId">Specified secret id.</param>
        /// <param name="permission">Specified permisiion.</param>
        /// <returns>Task, representing asynchronous operation.</returns>
        public Task<bool> IsAuthorizedAsync(string userId, string secretId, PermissionType permission);

        /// <summary>
        /// Add permission for specified user to access specified secret. This does not commit changes, need to save explicitly afterwards.
        /// </summary>
        /// <param name="userId">Specified user id.</param>
        /// <param name="secretId">Specified secret id.</param>
        /// <param name="permission">Specified permisiion.</param>
        /// <returns>Task, representing asynchronous operation.</returns>
        public Task SetPermissionAsync(string userId, string secretId, PermissionType permission);

        /// <summary>
        /// remove permission for specified user to access specified secret. This does not commit changes, need to save explicitly afterwards.
        /// </summary>
        /// <param name="userId">Specified user id.</param>
        /// <param name="secretId">Specified secret id.</param>
        /// <param name="permission">Specified permisiion.</param>
        /// <returns>Task, representing asynchronous operation.</returns>
        public Task UnsetPermissionAsync(string userId, string secretId, PermissionType permission);
    }
}
