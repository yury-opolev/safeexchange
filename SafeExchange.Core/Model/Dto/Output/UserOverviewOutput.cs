namespace SafeExchange.Core.Model.Dto.Output
{
    public class UserOverviewOutput
    {
        public string AadUpn { get; set; }
        public string DisplayName { get; set; }
        public string ContactEmail { get; set; }
        public bool Enabled { get; set; }
    }

    public class ApplicationAdminOverviewOutput
    {
        public string DisplayName { get; set; }
        public string AadClientId { get; set; }
        public string AadTenantId { get; set; }
        public string ContactEmail { get; set; }
        public bool Enabled { get; set; }
        public int OwnerCount { get; set; }
        public bool OwnersAttentionRequired { get; set; }
    }
}
