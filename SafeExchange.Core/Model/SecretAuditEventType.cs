/// <summary>
/// SecretAuditEventType
/// </summary>

namespace SafeExchange.Core.Model
{
    public enum SecretAuditEventType
    {
        SecretCreated = 1,
        SecretMetadataUpdated = 2,
        SecretDeleted = 3,
        PermissionGranted = 4,
        PermissionRevoked = 5,
        ContentRead = 6,
        ContentWritten = 7,
        ContentCommitted = 8,
        AccessRequested = 9,
        AccessRequestApproved = 10,
        AccessRequestDenied = 11,
    }
}
