namespace SafeExchange.Core.Streams
{
    using SafeExchange.Core.Model;

    public static class UploadModeResolver
    {
        public static UploadMode Resolve(
            ContentMetadata content,
            bool hashHeaderPresent,
            bool allowLegacy,
            bool ignoreHeader)
        {
            if (ignoreHeader)
            {
                return allowLegacy ? UploadMode.Legacy : UploadMode.Reject;
            }

            var hashedModeLocked = content.RunningHashState is { Length: > 0 };
            var legacyModeLocked = !hashedModeLocked && content.Chunks.Count > 0;

            if (hashHeaderPresent)
            {
                return legacyModeLocked ? UploadMode.Reject : UploadMode.Hashed;
            }

            if (hashedModeLocked)
            {
                return UploadMode.Reject;
            }

            return allowLegacy ? UploadMode.Legacy : UploadMode.Reject;
        }
    }
}
