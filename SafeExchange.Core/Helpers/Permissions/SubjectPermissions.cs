/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using Newtonsoft.Json;

    public class SubjectPermissions
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        public string PartitionKey { get; set; }

        public string SecretName { get; set; }

        public string SubjectName { get; set; }

        public bool CanRead { get; set; }

        public bool CanWrite { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRevokeAccess { get; set; }
    }
}
