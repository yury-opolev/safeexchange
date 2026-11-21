/// <summary>
/// CallerDenial
/// </summary>

namespace SafeExchange.Core.Callers
{
    using System.Net;

    /// <summary>
    /// Why a caller may not use the API, in the shape REST returns today: the HTTP status code and the
    /// <see cref="BaseResponseObject{T}"/> <c>status</c>, <c>subStatus</c> and <c>error</c> values.
    /// </summary>
    public sealed record CallerDenial(HttpStatusCode StatusCode, string Status, string Error, string SubStatus)
    {
        public static CallerDenial Forbidden(string error, string subStatus = "")
            => new(HttpStatusCode.Forbidden, "forbidden", error, subStatus);
    }
}
