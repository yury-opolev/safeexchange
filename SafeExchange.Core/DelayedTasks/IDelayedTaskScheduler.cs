/// <summary>
/// IDelayedTaskScheduler
/// </summary>

namespace SafeExchange.Core.DelayedTasks
{
	using System;

	public interface IDelayedTaskScheduler
	{
		public ValueTask ScheduleDelayedTaskAsync<T>(DelayedTaskType taskType, DateTime runAtUtc, T payload) where T: class;
	}
}

