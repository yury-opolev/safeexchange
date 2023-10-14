using System;

namespace SafeExchange.Core.DelayedTasks
{
	public class NullDelayedTaskScheduler : IDelayedTaskScheduler
	{
        public ValueTask ScheduleDelayedTask(DateTime runAt, DelayedTaskType taskType, object payload)
        {
            // no-op
            return ValueTask.CompletedTask;
        }
    }
}

