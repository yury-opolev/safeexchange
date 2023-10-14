using System;
namespace SafeExchange.Core.DelayedTasks
{
	public interface IDelayedTaskScheduler
	{
		public ValueTask ScheduleDelayedTask(DateTime runAt, DelayedTaskType taskType, object payload);
	}
}

