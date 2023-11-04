/// <summary>
/// IDelayedTaskScheduler
/// </summary>

namespace SafeExchange.Core.DelayedTasks
{
	using System;

	/// <summary>
	/// Scheduler to create delayed tasks.
	/// </summary>
	public interface IDelayedTaskScheduler
	{
		/// <summary>
		/// Create delayed task of specified type.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="taskType">Specified task type.</param>
		/// <param name="runAtUtc">Time when task should be run.</param>
		/// <param name="payload">Task payload.</param>
		/// <returns></returns>
		public ValueTask ScheduleDelayedTaskAsync<T>(DelayedTaskType taskType, DateTime runAtUtc, T payload) where T: class;
	}
}

