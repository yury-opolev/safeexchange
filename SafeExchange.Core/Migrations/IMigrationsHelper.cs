/// <summary>
/// IMigrationsHelper
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System;

    /// <summary>
    /// Helper class to run data migrations on the database.
    /// </summary>
    public interface IMigrationsHelper
    {
        /// <summary>
        /// Run specified migration.
        /// </summary>
        /// <param name="migrationId">Migration identifier.</param>
        /// <returns></returns>
        public Task RunMigrationAsync(string migrationId);
    }
}
