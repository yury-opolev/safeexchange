/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    public enum RequestStatus
    {
        None = 0,

        InProgress = 100,
        
        Approved = 200,
        
        Rejected = 300,

        Expired = 400
    }
}
