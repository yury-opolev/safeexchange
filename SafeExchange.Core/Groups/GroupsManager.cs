/// <summary>
/// IGroupsManager
/// </summary>

namespace SafeExchange.Core.Groups
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Utilities;
    using System;

    public class GroupsManager : IGroupsManager
    {
        private SafeExchangeDbContext dbContext;
        private ILogger<GroupsManager> logger;

        public GroupsManager(SafeExchangeDbContext dbContext, ILogger<GroupsManager> logger)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc/>
        public async Task<GroupDictionaryItem?> GetGroupAsync(string groupId)
        {
            var existingGroup = await this.dbContext.GroupDictionary.FirstOrDefaultAsync(g => g.GroupId.Equals(groupId));
            if (existingGroup == default)
            {
                this.logger.LogInformation($"Group '{groupId}' does not exist.");
            }

            return existingGroup;
        }

        /// <inheritdoc/>
        public async Task<GroupDictionaryItem?> TryFindGroupByMailAsync(string groupMail)
        {
            var existingGroup = await this.dbContext.GroupDictionary.FirstOrDefaultAsync(g => g.GroupMail.Equals(groupMail));
            if (existingGroup == default)
            {
                this.logger.LogInformation($"Group with mail '{groupMail}' does not exist.");
            }

            return existingGroup;
        }

        /// <inheritdoc/>
        public async Task<GroupDictionaryItem> PutGroupAsync(string groupId, GroupInput registrationInput, SubjectType subjectType, string subjectId)
        {
            var existingGroup = await this.dbContext.GroupDictionary.FirstOrDefaultAsync(g => g.GroupId.Equals(groupId));
            if (existingGroup != default)
            {
                return existingGroup;
            }

            var groupItem = new GroupDictionaryItem(groupId, registrationInput, $"{subjectType} {subjectId}");
            var result = await DbUtils.TryAddOrGetEntityAsync(
                async () =>
                {
                    var entity = await this.dbContext.GroupDictionary.AddAsync(groupItem);
                    await this.dbContext.SaveChangesAsync();
                    return entity.Entity;
                },
                async () =>
                {
                    this.dbContext.GroupDictionary.Remove(groupItem);
                    var existingGroupItem = await this.dbContext.GroupDictionary.FirstAsync(g => g.GroupId.Equals(groupId));
                    return existingGroupItem;
                },
                this.logger);

            return result;
        }

        /// <inheritdoc/>
        public async Task DeleteGroupAsync(string groupId)
        {
            var existingGroup = await this.dbContext.GroupDictionary.FirstOrDefaultAsync(g => g.GroupId.Equals(groupId));
            if (existingGroup == null)
            {
                this.logger.LogInformation($"Cannot delete group registration '{groupId}', as it does not exist.");
                return;
            }

            this.dbContext.GroupDictionary.Remove(existingGroup);
            await dbContext.SaveChangesAsync();
        }
    }
}
