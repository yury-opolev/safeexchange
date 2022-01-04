

namespace SafeExchange.Core.AzureAd
{
    using Microsoft.Identity.Client;
    using System;

    internal interface IConfidentialClientProvider
    {
        /// <summary>
        /// Get <see cref="IConfidentialClientApplication">IConfidentialClientApplication</see> for authentication against Azure AD.
        /// </summary>
        /// <returns>Azure AD confidential client.</returns>
        public IConfidentialClientApplication GetConfidentialClient();
    }
}
