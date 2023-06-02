/// <summary>
/// TokenHelper
/// </summary>

namespace SafeExchange.Core
{
    using System.Security.Claims;
    using SafeExchange.Core.Model;

    public static class SubjectHelper
	{
		public static (SubjectType type, string subjectId) GetSubjectInfo(ITokenHelper tokenHelper, ClaimsPrincipal principal)
		{
            var isUserToken = tokenHelper.IsUserToken(principal);
            return (
                isUserToken ? SubjectType.User : SubjectType.Application,
                isUserToken ? tokenHelper.GetUpn(principal) : tokenHelper.GetApplicationClientId(principal));
		}
	}
}

