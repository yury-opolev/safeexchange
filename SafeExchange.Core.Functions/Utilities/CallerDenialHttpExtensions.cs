/// <summary>
/// CallerDenialHttpExtensions
/// </summary>

namespace SafeExchange.Core.Callers
{
    using Microsoft.Azure.Functions.Worker.Http;
    using System;
    using System.Threading.Tasks;

    public static class CallerDenialHttpExtensions
    {
        /// <summary>
        /// Writes the denial as today's REST response: its status code and a
        /// <see cref="BaseResponseObject{T}"/> body with its status, subStatus and error.
        /// </summary>
        public static Task<HttpResponseData> ToResponseAsync(this CallerDenial denial, HttpRequestData request)
        {
            if (denial is null)
            {
                throw new ArgumentNullException(nameof(denial));
            }

            return ActionResults.CreateResponseAsync(
                request,
                denial.StatusCode,
                new BaseResponseObject<object> { Status = denial.Status, SubStatus = denial.SubStatus, Error = denial.Error });
        }
    }
}
