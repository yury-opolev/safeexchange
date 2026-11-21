/// <summary>
/// AuditEventHasher — pure SHA-256 canonical hash for SecretAuditEvent records.
/// </summary>

namespace SafeExchange.Core.Audit
{
    using SafeExchange.Core.Model;
    using System;
    using System.Globalization;
    using System.Security.Cryptography;
    using System.Text;

    public static class AuditEventHasher
    {
        /// <summary>
        /// Hashes the canonical form of an event. <paramref name="channel"/> and <paramref name="clientLabel"/>
        /// are appended as <c>|{channel}|{clientLabel}</c> only when one of them is non-empty, so events
        /// without an origin (all REST and system events, and every event written before origins existed)
        /// hash exactly as before.
        /// </summary>
        public static string ComputeHash(
            string auditInstanceId,
            long sequenceNumber,
            SecretAuditEventType eventType,
            DateTime occurredAtUtc,
            SubjectType actorSubjectType,
            string actorSubjectId,
            string actorDisplayName,
            string payloadJson,
            string prevHash,
            string? channel = null,
            string? clientLabel = null)
        {
            var canonical = string.Join('|',
                auditInstanceId ?? string.Empty,
                sequenceNumber.ToString(CultureInfo.InvariantCulture),
                eventType.ToString(),
                occurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                actorSubjectType.ToString(),
                actorSubjectId ?? string.Empty,
                actorDisplayName ?? string.Empty,
                payloadJson ?? string.Empty,
                prevHash ?? string.Empty);
            if (!string.IsNullOrEmpty(channel) || !string.IsNullOrEmpty(clientLabel))
            {
                canonical = string.Join('|', canonical, channel ?? string.Empty, clientLabel ?? string.Empty);
            }

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToBase64String(bytes);
        }
    }
}
