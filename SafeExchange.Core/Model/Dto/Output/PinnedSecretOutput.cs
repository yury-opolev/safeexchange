/// <summary>
/// PinnedSecretOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System.Collections.Generic;

    public class PinnedSecretOutput
    {
        public string SecretName { get; set; }

        public bool Exists { get; set; }

        public bool CanRead { get; set; }

        public bool CanWrite { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRevokeAccess { get; set; }

        public List<string> Tags { get; set; } = new List<string>();
    }
}
