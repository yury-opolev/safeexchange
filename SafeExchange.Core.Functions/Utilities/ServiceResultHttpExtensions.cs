/// <summary>
/// ServiceResultHttpExtensions
/// </summary>

namespace SafeExchange.Core.Services
{
    using Microsoft.Azure.Functions.Worker.Http;
    using System;
    using System.Net;
    using System.Threading.Tasks;

    public static class ServiceResultHttpExtensions
    {
        /// <summary>
        /// Writes the result as today's REST response: <c>200 {"status":"ok","result":"ok"}</c> on success,
        /// otherwise the failure's status code and a <see cref="BaseResponseObject{T}"/> with its status,
        /// subStatus, error and detail (as <c>result</c>).
        /// </summary>
        public static Task<HttpResponseData> ToResponseAsync(this ServiceResult result, HttpRequestData request)
        {
            if (result is null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            return result.IsOk
                ? ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK, new BaseResponseObject<string> { Status = ServiceStatus.Ok.ToStatusString(), Result = "ok" })
                : WriteFailureAsync(result, request);
        }

        /// <summary>
        /// Writes the result as today's REST response: <c>200</c> with the value as <c>result</c> on success,
        /// otherwise as <see cref="ToResponseAsync(ServiceResult, HttpRequestData)"/>.
        /// </summary>
        public static Task<HttpResponseData> ToResponseAsync<T>(this ServiceResult<T> result, HttpRequestData request)
        {
            if (result is null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            return result.IsOk
                ? ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK, new BaseResponseObject<T> { Status = ServiceStatus.Ok.ToStatusString(), Result = result.Value })
                : WriteFailureAsync(result.Outcome, request);
        }

        /// <summary>
        /// Writes a successful download as a raw <c>200</c> response (<c>Content-Type</c>, <c>Content-Length</c>,
        /// then <c>Content-Disposition</c> when present) and streams the bytes into its body; a failure is
        /// written as JSON like <see cref="ToResponseAsync(ServiceResult, HttpRequestData)"/>.
        /// </summary>
        public static async Task<HttpResponseData> ToDownloadResponseAsync(this ServiceResult<ContentDownload> result, HttpRequestData request)
        {
            if (result is null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            if (!result.IsOk)
            {
                return await WriteFailureAsync(result.Outcome, request);
            }

            var download = result.Value;
            var response = request.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", download.ContentType);
            response.Headers.Add("Content-Length", $"{download.ContentLength}");
            if (download.ContentDisposition is not null)
            {
                response.Headers.Add("Content-Disposition", download.ContentDisposition);
            }

            await download.WriteToAsync(response.Body);
            return response;
        }

        private static Task<HttpResponseData> WriteFailureAsync(ServiceResult failure, HttpRequestData request)
            => ActionResults.CreateResponseAsync(
                request,
                failure.StatusCode,
                new BaseResponseObject<object>
                {
                    Status = failure.Status.ToStatusString(),
                    SubStatus = failure.SubStatus,
                    Error = failure.Error,
                    Result = failure.Detail,
                });
    }
}
