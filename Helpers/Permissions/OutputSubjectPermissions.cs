/// SafeExchange

namespace SpaceOyster.SafeExchange
{
    public class OutputSubjectPermissions
    {
        public OutputSubjectPermissions(SubjectPermissions source)
        {
            this.UserName = source.SubjectName;

            this.CanRead = source.CanRead;
            this.CanWrite = source.CanWrite;
            this.CanGrantAccess = source.CanGrantAccess;
            this.CanRevokeAccess = source.CanRevokeAccess;
        }

        public string UserName { get; set; }

        public bool CanRead { get; set; }

        public bool CanWrite { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRevokeAccess { get; set; }
    }
}
