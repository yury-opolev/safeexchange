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
        /// empty or expired. Returns true when the user was modified (caller must save).</summary>
        public bool EnsureCurrent(User user, DateTime nowUtc)
        {
            if (user is null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            if (!string.IsNullOrEmpty(user.TelemetryId) && nowUtc < user.TelemetryIdExpiresAt)
            {
                return false;
            }

            user.TelemetryId = Guid.NewGuid().ToString("n");
            user.TelemetryIdExpiresAt = NextWeekBoundaryUtc(nowUtc);
            return true;
        }
    }
}
