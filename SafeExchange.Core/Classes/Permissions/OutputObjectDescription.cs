/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    public class OutputObjectDescription
    {
        public OutputObjectDescription(SubjectPermissions source)
        {
            this.ObjectName = source.SecretName;
            this.UserName = source.SubjectName;

            this.CanRead = source.CanRead;
            this.CanWrite = source.CanWrite;
            this.CanGrantAccess = source.CanGrantAccess;
            this.CanRevokeAccess = source.CanRevokeAccess;
        }

        public string ObjectName { get; set; }

        public string UserName { get; set; }

        public bool CanRead { get; set; }

        public bool CanWrite { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRevokeAccess { get; set; }
    }
}