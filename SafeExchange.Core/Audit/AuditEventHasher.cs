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
        public static string ComputeHash(
            string auditInstanceId,
            long sequenceNumber,
            SecretAuditEventType eventType,
            DateTime occurredAtUtc,
            SubjectType actorSubjectType,
            string actorSubjectId,
            string actorDisplayName,
            string payloadJson,
            string prevHash)
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
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToBase64String(bytes);
        }
    }
}
