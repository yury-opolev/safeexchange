
namespace SafeExchange.Core.Graph
{
    using SafeExchange.Core.Model.Dto.Output;
    using System;

    public class GraphGroupInfo
    {
        public GraphGroupInfo()
        {
            this.Id = string.Empty;
            this.DisplayName = string.Empty;
        }

        public GraphGroupInfo(string id, string displayName, string? mail)
        {
            this.Id = id ?? throw new ArgumentNullException(nameof(id));
            this.DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
            this.Mail = mail;
        }

        public string Id { get; set; }

        public string DisplayName { get; set; }

        public string? Mail { get; set; }

        internal GraphGroupOutput ToDto() => new()
        {
            Id = this.Id,
            DisplayName = this.DisplayName,
            Mail = this.Mail
        };
    }
}
