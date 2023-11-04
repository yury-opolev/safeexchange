/// <summary>
/// AccessRequestOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

    public class NotificationDataOutput
    {
        public string Url { get; set; }

        public List<string> RecipientUpns { get; set; }
    }
}
