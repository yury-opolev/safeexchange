/// <summary>
/// DefaultCredentialProvider
/// </summary>

namespace SafeExchange.Core
{
    using Azure.Core;
    using Azure.Identity;

    public static class DefaultCredentialProvider
    {
        public static TokenCredential CreateDefaultCredential()
        {
            return new ChainedTokenCredential(
#if DEBUG
                new ManagedIdentityCredential(), new EnvironmentCredential(), new VisualStudioCredential()
#else
                new ManagedIdentityCredential()
#endif
                );
        }
    }
}
