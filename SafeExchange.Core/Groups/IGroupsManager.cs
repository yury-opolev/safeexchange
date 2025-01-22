/// <summary>
/// IGroupsManager
/// </summary>

namespace SafeExchange.Core.Groups
{
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;

    /// <summary>
    /// Manager for handling groups.
    /// </summary>
    public interface IGroupsManager
    {
        /// <summary>
        /// Get existing group, return default value if group does not exist.
        /// </summary>
        /// <param name="groupId">Group Id.</param>
        /// <returns>Task, representing asynchronous operation.</returns>
        public Task<GroupDictionaryItem?> GetGroupAsync(string groupId);

        /// <summary>
        /// Put group, i.e. create group if it does not exist, and skip it if it already exists.
        /// </summary>
        /// <param name="groupId">Group Id.</param>
        /// <param name="registrationInput">Group details input.</param>
        /// <param name="subjectType">Type of subject which is trying to put the group.</param>
        /// <param name="subjectName">Name of subject which is trying to put the group.</param>
        /// <returns></returns>
        public Task<GroupDictionaryItem> PutGroupAsync(string groupId, GroupInput registrationInput, SubjectType subjectType, string subjectId);

        /// <summary>
        /// Delete group.
        /// </summary>
        /// <param name="groupId">Group Id.</param>
        /// <returns></returns>
        public Task DeleteGroupAsync(string groupId);
    }
}
