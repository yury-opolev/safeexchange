/// <summary>
/// AccessTicketConfiguration
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using System;

    public class AccessTicketConfiguration
    {
        public TimeSpan AccessTicketTimeout { get; set; }

        public AccessTicketConfiguration Clone()
        {
            return new AccessTicketConfiguration()
            {
                AccessTicketTimeout = this.AccessTicketTimeout
            };
        }

        public override bool Equals(object obj)
        {
            if (obj is not AccessTicketConfiguration other)
            {
                return false;
            }

            return this.AccessTicketTimeout.Equals(other.AccessTicketTimeout);
        }

        public override int GetHashCode() => HashCode.Combine(this.AccessTicketTimeout);
    }
}
