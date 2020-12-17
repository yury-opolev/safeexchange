/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.Azure.Cosmos.Table;

    public class SubjectPermissions : TableEntity
    {
        public string SecretName { get; set; }

        public string SubjectName { get; set; }

        public bool CanRead { get; set; }

        public bool CanWrite { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRevokeAccess { get; set; }
    }
}
