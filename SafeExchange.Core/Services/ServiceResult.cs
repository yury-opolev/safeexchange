/// <summary>
/// ServiceResult
/// </summary>

namespace SafeExchange.Core.Services
{
    using SafeExchange.Core.Permissions;
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Net;

    /// <summary>
    /// Result of a service call without a value. A failure carries everything REST writes today: the
    /// HTTP status code (the same <see cref="ServiceStatus"/> maps to different codes in places, e.g.
    /// <c>error</c> is 400 for invalid access input), the <c>status</c>, <c>subStatus</c> and <c>error</c>
    /// strings, and an optional <see cref="Detail"/> written as <c>result</c> (e.g. a hash mismatch).
    /// </summary>
    public sealed class ServiceResult
    {
        private static readonly ServiceResult OkResult = new(ServiceStatus.Ok, HttpStatusCode.OK, null, string.Empty, null);

        private ServiceResult(ServiceStatus status, HttpStatusCode statusCode, string? error, string subStatus, object? detail)
        {
            this.Status = status;
            this.StatusCode = statusCode;
            this.Error = error;
            this.SubStatus = subStatus;
            this.Detail = detail;
        }

        public ServiceStatus Status { get; }

        public HttpStatusCode StatusCode { get; }

        public string? Error { get; }

        public string SubStatus { get; }

        public object? Detail { get; }

        [MemberNotNullWhen(false, nameof(Error))]
        public bool IsOk => this.Status == ServiceStatus.Ok;

        public static ServiceResult Ok() => OkResult;

        public static ServiceResult Failure(ServiceStatus status, HttpStatusCode statusCode, string error, string subStatus = "", object? detail = null)
        {
            if (status == ServiceStatus.Ok)
            {
                throw new ArgumentException("A failure cannot have the Ok status.", nameof(status));
            }

            return new ServiceResult(
                status,
                statusCode,
                error ?? throw new ArgumentNullException(nameof(error)),
                subStatus ?? throw new ArgumentNullException(nameof(subStatus)),
                detail);
        }

        public static ServiceResult NotFound(string error) => Failure(ServiceStatus.NotFound, HttpStatusCode.NotFound, error);

        public static ServiceResult Forbidden(string error, string subStatus = "") => Failure(ServiceStatus.Forbidden, HttpStatusCode.Forbidden, error, subStatus);

        public static ServiceResult InsufficientPermissions(PermissionType permission, string secretId, string subStatus = "")
            => Forbidden($"Insufficient permissions to do '{permission}' action on '{secretId}'", subStatus);

        public static ServiceResult Unauthorized(string error) => Failure(ServiceStatus.Unauthorized, HttpStatusCode.Unauthorized, error);

        public static ServiceResult Unprocessable(string error) => Failure(ServiceStatus.Unprocessable, HttpStatusCode.UnprocessableEntity, error);

        public static ServiceResult BadRequest(string error) => Failure(ServiceStatus.BadRequest, HttpStatusCode.BadRequest, error);

        public static ServiceResult Conflict(string error) => Failure(ServiceStatus.Conflict, HttpStatusCode.Conflict, error);
    }

    /// <summary>
    /// Result of a service call that returns a <typeparamref name="T"/> on success. Failures are written as
    /// <see cref="ServiceResult"/> and convert implicitly, so a method returning <c>ServiceResult&lt;T&gt;</c>
    /// can <c>return ServiceResult.NotFound(...)</c>.
    /// </summary>
    public sealed class ServiceResult<T>
    {
        private ServiceResult(ServiceResult outcome, T? value)
        {
            this.Outcome = outcome;
            this.Value = value;
        }

        /// <summary>
        /// The status part of the result (the failure itself for a failed call).
        /// </summary>
        public ServiceResult Outcome { get; }

        public T? Value { get; }

        public ServiceStatus Status => this.Outcome.Status;

        public HttpStatusCode StatusCode => this.Outcome.StatusCode;

        public string? Error => this.Outcome.Error;

        public string SubStatus => this.Outcome.SubStatus;

        public object? Detail => this.Outcome.Detail;

        [MemberNotNullWhen(true, nameof(Value))]
        public bool IsOk => this.Outcome.IsOk;

        public static ServiceResult<T> Ok(T value)
            => new(ServiceResult.Ok(), value ?? throw new ArgumentNullException(nameof(value)));

        public static implicit operator ServiceResult<T>(ServiceResult failure)
        {
            if (failure is null)
            {
                throw new ArgumentNullException(nameof(failure));
            }

            if (failure.IsOk)
            {
                throw new InvalidOperationException($"An ok result needs a value; use {nameof(ServiceResult)}<T>.{nameof(Ok)}(value).");
            }

            return new ServiceResult<T>(failure, default);
        }
    }
}
