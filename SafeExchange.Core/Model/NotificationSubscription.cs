/// <summary>
/// NotificationSubscription
/// </summary>

namespace SafeExchange.Core.Model
{
    using SafeExchange.Core.Model.Dto.Input;
    using System;

    public class NotificationSubscription
    {
        public NotificationSubscription()
        { }

        public NotificationSubscription(string userUpn, NotificationSubscriptionCreationInput input)
        {
            this.Id = Guid.NewGuid().ToString();
            this.PartitionKey = this.GetPartitionKey();

            this.UserUpn = userUpn ?? throw new ArgumentNullException(nameof(userUpn));

            this.Url = input.Url ?? throw new ArgumentException($"{nameof(input.Url)} is null.");
            this.P256dh = input.P256dh ?? throw new ArgumentException($"{nameof(input.P256dh)} is null.");
            this.Auth = input.Auth ?? throw new ArgumentException($"{nameof(input.Auth)} is null.");
        }

        public string Id { get; set; }

        public string PartitionKey { get; set; }

        public string UserUpn { get; set; }

        public string Url { get; set; }

        public string P256dh { get; set; }

        public string Auth { get; set; }

        private string GetPartitionKey()
        {
            var hashString = this.Id.GetHashCode().ToString("0000");
            return hashString.Substring(hashString.Length - 4, 4);
        }

        public override string ToString()
        {
            return $"Id:{this.Id}, UserUpn:{this.UserUpn}, Endpoint:{this.Url?[..40]}...";
        }
    }
}
