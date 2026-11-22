/// <summary>
/// FunctionRouteTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using Microsoft.Azure.Functions.Worker;
    using NUnit.Framework;
    using SafeExchange.Functions;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text.Json;

    /// <summary>
    /// Pins the public URL of every HTTP trigger of the function app. host.json sets an empty route prefix (so the
    /// discovery documents can be served at the host root), hence every route spells out its own <c>api/</c>
    /// prefix and the effective path (route prefix + route) must stay byte-identical to what clients already use.
    /// The functions are read by reflection from the referenced SafeExchange.Functions assembly (the same attributes
    /// the Functions host indexes) and host.json is the file copied next to the test assembly.
    /// </summary>
    [TestFixture]
    public class FunctionRouteTests
    {
        private const string DefaultRoutePrefix = "api";

        private static readonly string[] ExpectedFunctions =
        {
            "SafeExchange-AccessGiveUp|delete,get|api/{apiVersion}/access-giveup/{secretId}",
            "SafeExchange-AccessRequest|delete,patch,post|api/{apiVersion}/accessrequest/{secretId}",
            "SafeExchange-Access|delete,get,patch,post|api/{apiVersion}/access/{secretId}",
            "SafeExchange-Admin-Apps-Delete|delete|api/{apiVersion}/admin/applications/{displayName}",
            "SafeExchange-Admin-Apps-Detail|get|api/{apiVersion}/admin/applications/{displayName}",
            "SafeExchange-Admin-Apps-List|get|api/{apiVersion}/admin/applications",
            "SafeExchange-Admin-Apps-ReplaceOwners|put|api/{apiVersion}/admin/applications/{displayName}/owners",
            "SafeExchange-Admin-Apps-ToggleEnabled|patch|api/{apiVersion}/admin/applications/{displayName}/enabled",
            "SafeExchange-Admin-Audit-Instance|get|api/{apiVersion}/admin/audit/{auditInstanceId}",
            "SafeExchange-Admin-Audit-Search|get|api/{apiVersion}/admin/audit",
            "SafeExchange-Admin-Secrets-Access|get|api/{apiVersion}/admin/secret/{secretName}/access",
            "SafeExchange-Admin-Secrets-Detail|get|api/{apiVersion}/admin/secret/{secretName}",
            "SafeExchange-Admin-Secrets-List|get|api/{apiVersion}/admin/secret-list",
            "SafeExchange-Admin-Users-ByTelemetryId|get|api/{apiVersion}/admin/users/by-telemetry-id/{telemetryId}",
            "SafeExchange-Admin-Users-Detail|get|api/{apiVersion}/admin/users/{upn}",
            "SafeExchange-Admin-Users-List|get|api/{apiVersion}/admin/users",
            "SafeExchange-Admin-Users-ToggleEnabled|patch|api/{apiVersion}/admin/users/{upn}/enabled",
            "SafeExchange-AdminOperations|post|api/{apiVersion}/admops/{operationName}",
            "SafeExchange-ApplicationList|get|api/{apiVersion}/applications-list",
            "SafeExchange-ApplicationSearch|post|api/{apiVersion}/application-search",
            "SafeExchange-Application|delete,get,patch,post|api/{apiVersion}/applications/{applicationId}",
            "SafeExchange-ExternalNotificationDetails|get|api/v1/notificationdetails/{webhookNotificationDataId}",
            "SafeExchange-GroupSearch|post|api/{apiVersion}/group-search",
            "SafeExchange-GroupsList|get|api/{apiVersion}/groups-list",
            "SafeExchange-ListAccessRequests|get|api/{apiVersion}/accessrequest-list",
            "SafeExchange-ListSecretMeta|get|api/{apiVersion}/secret-list",
            "SafeExchange-Mcp|delete,get,post|api/mcp",
            "SafeExchange-OAuthAuthorizationServerMetadata|get|api/.well-known/oauth-authorization-server",
            "SafeExchange-OAuthAuthorize|get|api/oauth/authorize",
            "SafeExchange-OAuthCallback|get|api/oauth/callback",
            "SafeExchange-OAuthOpenIdConfiguration|get|api/.well-known/openid-configuration",
            "SafeExchange-OAuthProtectedResource|get|api/.well-known/oauth-protected-resource",
            "SafeExchange-OAuthRegister|post|api/oauth/register",
            "SafeExchange-OAuthToken|post|api/oauth/token",
            "SafeExchange-PinnedGroupsList|get|api/{apiVersion}/pinnedgroups-list",
            "SafeExchange-PinnedGroups|delete,get,put|api/{apiVersion}/pinnedgroups/{pinnedGroupId}",
            "SafeExchange-PinnedSecretsList|get|api/{apiVersion}/pinnedsecrets-list",
            "SafeExchange-PinnedSecrets|delete,get,put|api/{apiVersion}/pinnedsecrets/{secretId}",
            "SafeExchange-S2SApps-AddOwner|post|api/{apiVersion}/s2sapps/{displayName}/owners",
            "SafeExchange-S2SApps-AllowedTenants|get|api/{apiVersion}/s2sapps-allowed-tenants",
            "SafeExchange-S2SApps-Delete|delete|api/{apiVersion}/s2sapps/{displayName}",
            "SafeExchange-S2SApps-Detail|get|api/{apiVersion}/s2sapps/{displayName}",
            "SafeExchange-S2SApps-ListOwners|get|api/{apiVersion}/s2sapps/{displayName}/owners",
            "SafeExchange-S2SApps-Mine|get|api/{apiVersion}/me/s2sapps",
            "SafeExchange-S2SApps-Register|post|api/{apiVersion}/s2sapps",
            "SafeExchange-S2SApps-RemoveOwner|delete|api/{apiVersion}/s2sapps/{displayName}/owners/{subjectType}/{subjectId}",
            "SafeExchange-S2SApps-ReplaceOwners|put|api/{apiVersion}/s2sapps/{displayName}/owners",
            "SafeExchange-S2SApps-ToggleEnabled|patch|api/{apiVersion}/s2sapps/{displayName}/enabled",
            "SafeExchange-SecretAudit|get|api/{apiVersion}/secret/{secretId}/audit",
            "SafeExchange-SecretContentCommit|patch|api/{apiVersion}/secret/{secretId}/content/{contentId}/commit",
            "SafeExchange-SecretContentMetaCreate|post|api/{apiVersion}/secret/{secretId}/content",
            "SafeExchange-SecretContentMetaDrop|patch|api/{apiVersion}/secret/{secretId}/content/{contentId}/drop",
            "SafeExchange-SecretContentMeta|delete,patch|api/{apiVersion}/secret/{secretId}/content/{contentId}",
            "SafeExchange-SecretMeta|delete,get,patch,post|api/{apiVersion}/secret/{secretId}",
            "SafeExchange-SecretStreamContentDownload|get|api/{apiVersion}/secret/{secretId}/content/{contentId}/all",
            "SafeExchange-SecretStreamDownload|get|api/{apiVersion}/secret/{secretId}/content/{contentId}/chunk/{chunkId}",
            "SafeExchange-SecretStreamUpload|post|api/{apiVersion}/secret/{secretId}/content/{contentId}/chunk",
            "SafeExchange-TelemetryConfig|get|api/{apiVersion}/telemetry/config",
            "SafeExchange-UserSearch|post|api/{apiVersion}/user-search",
            "SafeExchange-WebhookSubscriptionCreate|post|api/v1/webhooksubscriptions",
            "SafeExchange-WebhookSubscriptionList|get|api/{apiVersion}/webhooksubscriptions-list",
            "SafeExchange-WebhookSubscription|delete,get,patch|api/v1/webhooksubscriptions/{webhookSubscriptionId}",

            // Root routes for OAuth discovery (the platform answers 502 to anything outside the route prefix).
            "SafeExchange-WellKnownAuthorizationServerApi|get|.well-known/oauth-authorization-server/api",
            "SafeExchange-WellKnownOpenIdConfigurationApi|get|.well-known/openid-configuration/api",
            "SafeExchange-WellKnownProtectedResourceApiMcp|get|.well-known/oauth-protected-resource/api/mcp",
            "SafeExchange-WellKnownNotFound|get|.well-known/{*rest:regex(^(?!(oauth-authorization-server/api|openid-configuration/api|oauth-protected-resource/api/mcp)$).*$)}",
        };

        [Test]
        public void HostJson_SetsAnEmptyRoutePrefix()
        {
            Assert.That(ReadRoutePrefix(), Is.EqualTo(string.Empty));
        }

        [Test]
        public void EveryHttpTrigger_IsServedAtItsPinnedPublicPath()
        {
            Assert.That(DiscoverFunctions(ReadRoutePrefix()), Is.EquivalentTo(ExpectedFunctions));
        }

        [Test]
        public void EveryRoute_ExceptTheWellKnownRootOnes_StaysUnderApi()
        {
            var outsideApi = DiscoverFunctions(ReadRoutePrefix())
                .Select(f => f.Split('|'))
                .Where(f => !f[2].StartsWith("api/", StringComparison.Ordinal) && !f[2].StartsWith(".well-known/", StringComparison.Ordinal));

            Assert.That(outsideApi, Is.Empty);
        }

        // name|methods|path, where path is the route prefix from host.json joined with the trigger route.
        private static List<string> DiscoverFunctions(string routePrefix)
        {
            var functions = typeof(SafeMcp).Assembly.GetExportedTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Select(method => (Function: method.GetCustomAttribute<FunctionAttribute>(), Trigger: method.GetParameters()
                    .Select(parameter => parameter.GetCustomAttribute<HttpTriggerAttribute>())
                    .FirstOrDefault(attribute => attribute != null)))
                .Where(entry => entry.Function != null && entry.Trigger != null)
                .Select(entry =>
                {
                    var methods = string.Join(",", (entry.Trigger!.Methods ?? Array.Empty<string>()).Select(m => m.ToLowerInvariant()).OrderBy(m => m, StringComparer.Ordinal));
                    var route = (entry.Trigger.Route ?? string.Empty).Trim('/');
                    var path = routePrefix.Length == 0 ? route : $"{routePrefix.Trim('/')}/{route}";
                    return $"{entry.Function!.Name}|{methods}|{path}";
                })
                .ToList();

            Assert.That(functions, Is.Not.Empty);
            return functions;
        }

        private static string ReadRoutePrefix()
        {
            var hostJsonPath = Path.Combine(AppContext.BaseDirectory, "host.json");
            Assert.That(File.Exists(hostJsonPath), Is.True, "host.json of SafeExchange.Functions is expected next to the test assembly.");

            using var document = JsonDocument.Parse(File.ReadAllText(hostJsonPath));
            if (document.RootElement.TryGetProperty("extensions", out var extensions)
                && extensions.TryGetProperty("http", out var http)
                && http.TryGetProperty("routePrefix", out var routePrefix))
            {
                return routePrefix.GetString() ?? string.Empty;
            }

            return DefaultRoutePrefix;
        }
    }
}
