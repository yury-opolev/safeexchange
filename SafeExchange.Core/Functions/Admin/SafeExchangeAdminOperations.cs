/// <summary>
/// SafeExchangeAdminOperations
/// </summary>

namespace SafeExchange.Core.Functions.Admin
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Migrations;
    using SafeExchange.Core.Model;
    using System;
    using System.Net;
    using System.Security.Claims;

    public class SafeExchangeAdminOperations
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly ICryptoHelper cryptoHelper;

        private readonly IMigrationsHelper migrationsHelper;

        private readonly GlobalFilters globalFilters;

        public SafeExchangeAdminOperations(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, ICryptoHelper cryptoHelper, IMigrationsHelper migrationsHelper, GlobalFilters globalFilters)
        {
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.cryptoHelper = cryptoHelper ?? throw new ArgumentNullException(nameof(cryptoHelper));
            this.migrationsHelper = migrationsHelper ?? throw new ArgumentNullException(nameof(migrationsHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string operationName, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (SubjectType.Application.Equals(subjectType))
            {
                await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    new BaseResponseObject<object> { Status = "forbidden", Error = "Applications cannot use this API." });
            }

            log.LogInformation($"{nameof(SafeExchangeAdminOperations)} triggered for operation '{operationName}' by {subjectType} {subjectId}, [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "post":
                    return await this.PerformOperationAsync(operationName, request, log);

                default:
                    var response = await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.InternalServerError,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
                    return response;
            }
        }

        private async Task<HttpResponseData> PerformOperationAsync(string operationName, HttpRequestData request, ILogger log)
            => await TryCatch(request, async () =>
        {
            switch (operationName)
            {
                // no-op, cannot create containers via DbContext with managed identities
                //case "ensure_dbcreated":
                //    await this.dbContext.Database.EnsureCreatedAsync();
                //    break;

                case "add_kek_version":
                    var cryptoConfiguration = this.cryptoHelper.CryptoConfiguration;
                    await this.cryptoHelper.CreateNewCryptoKeyVersionAsync(cryptoConfiguration.KeyName);
                    break;

                case "run_dbmigration":
                    return await this.RunDbMigrationAsync(request, log);
            }

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<object> { Status = "ok", Result = "ok" });
        }, nameof(PerformOperationAsync), log);

        private async Task<HttpResponseData> RunDbMigrationAsync(HttpRequestData request, ILogger log)
        {
            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            MigrationInput? migrationInput;
            try
            {
                migrationInput = DefaultJsonSerializer.Deserialize<MigrationInput>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for migration.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Migration details are not provided." });
            }

            if (string.IsNullOrEmpty(migrationInput?.MigrationId))
            {
                log.LogInformation($"ID for migration is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Migration ID is not provided." });
            }

            await this.migrationsHelper.RunMigrationAsync(migrationInput.MigrationId);

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<object> { Status = "ok", Result = "ok" });
        }

        private static async Task<HttpResponseData> TryCatch(HttpRequestData request, Func<Task<HttpResponseData>> action, string actionName, ILogger log)
        {
            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, $"Exception in {actionName}: {ex.GetType()}: {ex.Message}.");

                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.InternalServerError,
                    new BaseResponseObject<object> { Status = "error", SubStatus = "error", Error = $"{ex.GetType()}: {ex.Message ?? "Unknown exception."}" });
            }
        }
    }
}
