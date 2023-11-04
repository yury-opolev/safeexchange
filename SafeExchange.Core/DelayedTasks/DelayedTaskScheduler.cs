/// <summary>
/// DelayedTaskScheduler
/// </summary>

namespace SafeExchange.Core.DelayedTasks
{
    using Microsoft.Extensions.Logging;

	public class DelayedTaskScheduler : IDelayedTaskScheduler
	{
        private readonly ILogger<DelayedTaskScheduler> log;

        private readonly IQueueHelper queueHelper;

        public DelayedTaskScheduler(IQueueHelper queueHelper, ILogger<DelayedTaskScheduler> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.queueHelper = queueHelper ?? throw new ArgumentNullException(nameof(queueHelper));
        }

        public async ValueTask ScheduleDelayedTaskAsync<T>(DelayedTaskType taskType, DateTime runAtUtc, T payload) where T: class
        {
            switch (taskType)
            {
                case DelayedTaskType.ExternalNotification:
                    await this.ScheduleDelayedExternalNotificationAsync(runAtUtc, payload);
                    return;

                default:
                    throw new NotImplementedException();
            }
        }

        private async Task ScheduleDelayedExternalNotificationAsync<T>(DateTime runAtUtc, T payload) where T : class
        {
            this.log.LogInformation($"{nameof(ScheduleDelayedExternalNotificationAsync)} started.");
            var utcNow = DateTimeProvider.UtcNow;
            var delay = runAtUtc - utcNow;
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            await this.queueHelper.EnqueueMessageAsync(payload, delay);
        }
    }
}
