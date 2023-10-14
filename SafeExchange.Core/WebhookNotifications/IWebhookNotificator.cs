using System;
using SafeExchange.Core.Model;

namespace SafeExchange.Core.WebhookNotifications
{
	public interface IWebhookNotificator
	{
		public ValueTask TryNotifyAsync(AccessRequest accessRequest, IList<string> recipients);
	}
}

