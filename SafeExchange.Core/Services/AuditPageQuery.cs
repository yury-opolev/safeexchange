/// <summary>
/// AuditPageQuery
/// </summary>

namespace SafeExchange.Core.Services
{
    using System;
    using System.Globalization;

    /// <summary>
    /// One page request against a secret's audit trail. <see cref="Parse"/> reads the REST query string
    /// (<c>from</c>, <c>to</c>, <c>raw</c>, <c>pageSize</c>, <c>continuation</c>, <c>direction</c>).
    /// </summary>
    public sealed record AuditPageQuery
    {
        public const int DefaultPageSize = 100;

        public const int MaxPageSize = 500;

        /// <summary>
        /// Inclusive lower bound on <c>OccurredAt</c> (UTC).
        /// </summary>
        public DateTime? From { get; init; }

        /// <summary>
        /// Exclusive upper bound on <c>OccurredAt</c> (UTC).
        /// </summary>
        public DateTime? To { get; init; }

        /// <summary>
        /// <c>true</c> returns every event; <c>false</c> merges runs of content reads.
        /// </summary>
        public bool Raw { get; init; }

        /// <summary>
        /// Requested page size; clamped to 1..<see cref="MaxPageSize"/> when the page is built.
        /// </summary>
        public int PageSize { get; init; } = DefaultPageSize;

        /// <summary>
        /// The previous page's <c>nextContinuation</c>; an unreadable token starts from the beginning.
        /// </summary>
        public string? Continuation { get; init; }

        public bool Descending { get; init; }

        public static AuditPageQuery Parse(string? queryString)
        {
            var qs = System.Web.HttpUtility.ParseQueryString(queryString ?? string.Empty);
            return new AuditPageQuery
            {
                From = TryParseUtc(qs["from"]),
                To = TryParseUtc(qs["to"]),
                Raw = bool.TryParse(qs["raw"], out var r) && r,
                PageSize = int.TryParse(qs["pageSize"], out var p) ? p : DefaultPageSize,
                Continuation = qs["continuation"],
                Descending = string.Equals(qs["direction"], "desc", StringComparison.OrdinalIgnoreCase),
            };
        }

        private static DateTime? TryParseUtc(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            return DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt) ? dt : null;
        }
    }
}
