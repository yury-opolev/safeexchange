/// <summary>
/// SubjectHelper
/// </summary>

namespace SafeExchange.Core
{
    using System.Security.Claims;
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Core.Model;

    public static class SubjectHelper
    {
		public static async Task<(SubjectType type, string subjectId)> GetSubjectInfoAsync(ITokenHelper tokenHelper, ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
		{
            if (tokenHelper.IsUserToken(principal))
            {
                return (SubjectType.User, tokenHelper.GetUpn(principal));
            }

            var displayName = await GetApplicationDisplayNameAsync(
                tokenHelper.GetTenantId(principal), tokenHelper.GetApplicationClientId(principal), dbContext);
            return (SubjectType.Application, displayName);
		}

        public static async Task<string> GetApplicationDisplayNameAsync(string? tenantId, string? clientId, SafeExchangeDbContext dbContext)
        {
            if (string.IsNullOrEmpty(tenantId))
            {
                return string.Empty;
            }

            if (string.IsNullOrEmpty(clientId))
            {
                return string.Empty;
            }

            var existingApplication = await dbContext.Applications.FirstOrDefaultAsync(a => a.Enabled && a.AadClientId.Equals(clientId) && a.AadTenantId.Equals(tenantId));
            if (existingApplication == default)
            {
                return string.Empty;
            }

            return existingApplication.DisplayName;
        }
	}
}

