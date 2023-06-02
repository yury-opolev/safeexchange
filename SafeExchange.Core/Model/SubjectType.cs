/// <summary>
/// SubjectType
/// </summary>

namespace SafeExchange.Core.Model
{
    using System;

    public enum SubjectType
	{
		/// <summary>
		/// Subject is a user.
		/// </summary>
		User = 0,

		/// <summary>
		/// Subject is an applicaiton, i.e. service or a daemon.
		/// </summary>
		Application = 100
	}
}

