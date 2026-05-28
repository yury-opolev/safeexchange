/// <summary>
/// ApplicationOwnerInvariantException — thrown when an attempted owner-set
/// change would leave the application below the ownership invariant
/// (see docs/SPIKE-s2s-apps.md). Carries a user-presentable message; the
/// endpoint layer translates this to a 409 with the message echoed.
/// </summary>

namespace SafeExchange.Core.Applications
{
    using System;

    public class ApplicationOwnerInvariantException : Exception
    {
        public ApplicationOwnerInvariantException(string message)
            : base(message)
        {
        }
    }
}
