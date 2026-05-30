/// <summary>
/// TelemetryIdRotationResult
/// </summary>

namespace SafeExchange.Core.Telemetry
{
    using System;

    /// <summary>Outcome of <see cref="TelemetryIdRotator.EnsureCurrent"/>. When a real
    /// rotation retired a previous id, <see cref="RetiredTelemetryId"/> is non-null and
    /// the validity window describes when that id was active.</summary>
    public readonly struct TelemetryIdRotationResult
    {
        public TelemetryIdRotationResult(bool rotated, string? retiredTelemetryId, DateTime retiredValidFromUtc, DateTime retiredValidToUtc)
        {
            this.Rotated = rotated;
            this.RetiredTelemetryId = retiredTelemetryId;
            this.RetiredValidFromUtc = retiredValidFromUtc;
            this.RetiredValidToUtc = retiredValidToUtc;
        }

        /// <summary>True when a new id was generated (caller must persist).</summary>
        public bool Rotated { get; }

        /// <summary>The id just retired, or null on first-ever creation / no-op.</summary>
        public string? RetiredTelemetryId { get; }

        public DateTime RetiredValidFromUtc { get; }

        public DateTime RetiredValidToUtc { get; }
    }
}
