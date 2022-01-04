/// <summary>
/// RequestStatus
/// </summary>

namespace SafeExchange.Core.Model
{
    using System;

    public enum RequestStatus
    {
        None = 0,

        InProgress = 100,

        Approved = 200,

        Rejected = 300,

        Expired = 400
    }
}
