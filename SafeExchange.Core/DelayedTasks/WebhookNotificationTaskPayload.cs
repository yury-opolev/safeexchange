﻿/// <summary>
/// WebhookNotificationTaskPayload
/// </summary>

namespace SafeExchange.Core.DelayedTasks
{
    using SafeExchange.Core.Model;

    public class WebhookNotificationTaskPayload : DelayedTaskPayload
	{
        public const string AccessRequestCreatedSubType = "AccessRequestCreated";

        public WebhookNotificationTaskPayload()
        {
            this.TaskType = DelayedTaskType.ExternalNotification;
        }

        public string SubType { get; set; }

        public string AccessRequestId { get; set; }
    }
}

