/// <summary>
/// IQueueHelper
/// </summary>

namespace SafeExchange.Core
{
	using System;
    using Azure.Core;

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
		public ValueTask EnqueueMessageAsync<T>(T messageObject, TimeSpan visibilityTimeout) where T: class;

		/// <summary>
        /// Dequeue and delete message from azure queue, and deserialize to specified type. Asynchronous.
        /// </summary>
		/// <returns>A (bool succeded. T? result) tuple, where first item is true if message object was dequeued successfully.</returns>
		public ValueTask<(bool succeeded, T? result)> TryPopMessageAsync<T>() where T: class;
	}
}

