/// <summary>
/// TelemetryIdRotator
/// </summary>

namespace SafeExchange.Core.Telemetry
{
    using SafeExchange.Core.Model;
    using System;

    /// <summary>Pure, dependency-free rotation logic for a user's telemetry id.
    /// Calendar-aligned: ids expire at the start of the next week boundary
    /// (Monday 00:00 UTC), so every user rotates at the same instant.</summary>
    public sealed class TelemetryIdRotator
    {
        private const DayOfWeek WeekBoundaryDay = DayOfWeek.Monday;

        /// <summary>Returns the start of the next <see cref="WeekBoundaryDay"/> (UTC),
        /// strictly after <paramref name="nowUtc"/>'s date.</summary>
        public static DateTime NextWeekBoundaryUtc(DateTime nowUtc)
        {
            var date = nowUtc.Date;
            int days = ((int)WeekBoundaryDay - (int)date.DayOfWeek + 7) % 7;
            if (days == 0)
            {
                days = 7;
            }

            return DateTime.SpecifyKind(date.AddDays(days), DateTimeKind.Utc);
        }

        /// <summary>Ensures the user has a current telemetry id, regenerating it when
        /// empty or expired. Returns the rotation outcome; when a non-empty id was
        /// replaced, the result carries the retired id and its active window so the
        /// caller can record it in the TelemetryIdMap.</summary>
        public TelemetryIdRotationResult EnsureCurrent(User user, DateTime nowUtc)
        {
            if (user is null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            if (!string.IsNullOrEmpty(user.TelemetryId) && nowUtc < user.TelemetryIdExpiresAt)
            {
                return new TelemetryIdRotationResult(false, null, default, default);
            }

            string? retiredId = null;
            DateTime retiredFrom = default;
            DateTime retiredTo = default;
            if (!string.IsNullOrEmpty(user.TelemetryId))
            {
                retiredId = user.TelemetryId;
                retiredFrom = user.TelemetryIdIssuedAt;
                retiredTo = nowUtc;
            }

            user.TelemetryId = Guid.NewGuid().ToString("n");
            user.TelemetryIdIssuedAt = nowUtc;
            user.TelemetryIdExpiresAt = NextWeekBoundaryUtc(nowUtc);
            return new TelemetryIdRotationResult(true, retiredId, retiredFrom, retiredTo);
        }
    }
}
