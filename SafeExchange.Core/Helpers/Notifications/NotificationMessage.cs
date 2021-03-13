/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using System;

    public class NotificationMessage
    {
        public string From { get; set; }

        public string Topic { get; set; }

        public string MessageText { get; set; }

        public string Uri { get; set; }
    }
}
