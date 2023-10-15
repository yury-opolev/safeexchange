/// <summary>
/// IQueueHelper
/// </summary>

namespace SafeExchange.Core
{
	using System;

    /// <summary>
    /// Interface to provide azure storage queue operaions.
    /// </summary>
	public interface IQueueHelper
	{
		/// <summary>
        /// Enqueue specified message object serialized to json, in azure queue. Asynchronous.
        /// </summary>
        /// <param name="messageObject">Message object tovenqueue.</param>
		/// <param name="visibilityTimeout">Timeout for specified message to become visible in the queue.</param>
		/// <returns>A task representing asynchronous action.</returns>
		public ValueTask EnqueueAsync<T>(T messageObject, TimeSpan visibilityTimeout);
	}
}

