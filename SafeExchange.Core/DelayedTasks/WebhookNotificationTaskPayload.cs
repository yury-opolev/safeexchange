
namespace SafeExchange.Core.DelayedTasks
{
    using SafeExchange.Core.Model;

    public class WebhookNotificationTaskPayload
	{
        public string AccessRequestId { get; set; }

        public WebhookSubscription WebhookSubscription { get; set; }
    }
}

