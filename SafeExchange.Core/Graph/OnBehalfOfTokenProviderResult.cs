
namespace SafeExchange.Core.Graph
{
    using System;

    public class OnBehalfOfTokenProviderResult
    {
        public bool Success { get; set; }

        public bool ConsentRequired { get; set; }

        public string Token { get; set; } = string.Empty;

        public DateTimeOffset ExpiresOn { get; set; } = DateTimeOffset.MinValue;
    }
}
