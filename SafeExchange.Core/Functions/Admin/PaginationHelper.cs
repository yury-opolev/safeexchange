/// <summary>
/// PaginationHelper — uniform parsing of `?page=&pageSize=` query params with
/// clamping against the configured Limits.AdminListMaxPageSize. Shared across
/// admin paginated endpoints so behaviour is identical everywhere.
/// </summary>

namespace SafeExchange.Core.Functions.Admin
{
    using Microsoft.Azure.Functions.Worker.Http;
    using SafeExchange.Core.Configuration;
    using System;

    internal static class PaginationHelper
    {
        public static (int page, int pageSize) Parse(HttpRequestData request, Limits limits)
        {
            var pageRaw = request.Query["page"];
            var sizeRaw = request.Query["pageSize"];

            int page = 0;
            if (!string.IsNullOrWhiteSpace(pageRaw) && int.TryParse(pageRaw, out var p) && p >= 0)
            {
                page = p;
            }

            int pageSize = limits.AdminListDefaultPageSize;
            if (!string.IsNullOrWhiteSpace(sizeRaw) && int.TryParse(sizeRaw, out var s) && s > 0)
            {
                pageSize = Math.Min(s, limits.AdminListMaxPageSize);
            }

            return (page, pageSize);
        }
    }
}
