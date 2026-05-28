/// <summary>
/// PaginatedResult — uniform pagination envelope used by every admin list
/// endpoint. Kept generic so the same client-side pager component handles
/// users, applications, and audit results.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System.Collections.Generic;

    public class PaginatedResult<T>
    {
        public List<T> Items { get; set; } = new();

        public int Page { get; set; }

        public int PageSize { get; set; }

        public int Total { get; set; }

        public bool HasMore { get; set; }
    }
}
