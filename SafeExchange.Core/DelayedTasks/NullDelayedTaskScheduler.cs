/// <summary>
/// NullDelayedTaskScheduler
/// </summary>

namespace SafeExchange.Core.DelayedTasks
{
    using System;

	public class NullDelayedTaskScheduler : IDelayedTaskScheduler
	{
        public async ValueTask ScheduleDelayedTaskAsync<T>(DelayedTaskType taskType, DateTime runAtUtc, T payload) where T: class
        {
            // no-op
            await ValueTask.CompletedTask;
        }
    }
}

