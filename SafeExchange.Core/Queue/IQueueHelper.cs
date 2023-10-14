using System;
namespace SafeExchange.Core.Queue
{
	public interface IQueueHelper
	{
		public ValueTask EnqueueAsync();
	}
}

