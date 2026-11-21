/// <summary>
/// AuditOrigin
/// </summary>

namespace SafeExchange.Core.Audit
{
    using SafeExchange.Core.Callers;
    using System;
    using System.Text;

    /// <summary>
    /// Where an audited action came from: the channel (<c>mcp</c>) and the client's self-reported label.
    /// REST and system actions use <see cref="None"/>, so their events and hashes are unchanged.
    /// The label carries no authority; the trusted actor is the event's ActorSubjectType/ActorSubjectId.
    /// </summary>
    public sealed record AuditOrigin
    {
        public const int MaxLength = 128;

        public static AuditOrigin None { get; } = new(string.Empty, string.Empty);

        private AuditOrigin(string channel, string clientLabel)
        {
            this.Channel = channel;
            this.ClientLabel = clientLabel;
        }

        public string Channel { get; }

        public string ClientLabel { get; }

        public bool IsEmpty => this.Channel.Length == 0 && this.ClientLabel.Length == 0;

        public static AuditOrigin Create(string? channel, string? clientLabel)
        {
            var sanitizedChannel = Sanitize(channel);
            var sanitizedLabel = Sanitize(clientLabel);
            return sanitizedChannel.Length == 0 && sanitizedLabel.Length == 0
                ? None
                : new AuditOrigin(sanitizedChannel, sanitizedLabel);
        }

        /// <summary>
        /// MCP callers get their channel and label recorded; every other channel (REST) records nothing.
        /// </summary>
        public static AuditOrigin FromCaller(Caller caller)
        {
            ArgumentNullException.ThrowIfNull(caller);
            return string.Equals(caller.Channel, CallerChannels.Mcp, StringComparison.Ordinal)
                ? Create(caller.Channel, caller.ClientLabel)
                : None;
        }

        /// <summary>
        /// Keeps printable ASCII (0x20-0x7E) only, trims, and truncates to <see cref="MaxLength"/> characters.
        /// </summary>
        public static string Sanitize(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (c >= 0x20 && c <= 0x7E)
                {
                    builder.Append(c);
                }
            }

            var printable = builder.ToString().Trim();
            return printable.Length > MaxLength ? printable[..MaxLength].TrimEnd() : printable;
        }
    }
}
