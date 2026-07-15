using System;

namespace direct_module.Database
{
    /// <summary>
    /// Keeps untrusted wire timestamps from controlling history ordering or retention.
    /// Incoming messages are real-time protocol events, so their durable timestamp is
    /// the local receive time. Locally-created timestamps are retained when sane.
    /// </summary>
    public static class HistoryTimestampPolicy
    {
        public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);

        public static DateTime NormalizeForStorage(
            DateTime claimedTime,
            bool isOutgoing,
            DateTime utcNow)
        {
            DateTime safeNow = NormalizeUtcNow(utcNow);
            if (!isOutgoing)
            {
                return safeNow;
            }

            DateTime candidate;
            try
            {
                candidate = claimedTime.Kind switch
                {
                    DateTimeKind.Utc => claimedTime,
                    DateTimeKind.Local => claimedTime.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(claimedTime, DateTimeKind.Local).ToUniversalTime()
                };
            }
            catch (ArgumentException)
            {
                return safeNow;
            }

            DateTime latest = safeNow <= DateTime.MaxValue - MaximumFutureSkew
                ? safeNow + MaximumFutureSkew
                : DateTime.MaxValue;
            return candidate < DateTime.UnixEpoch || candidate > latest
                ? safeNow
                : DateTime.SpecifyKind(candidate, DateTimeKind.Utc);
        }

        public static DateTime NormalizePersistedForDisplay(DateTime parsed, DateTime utcNow)
        {
            DateTime safeNow = NormalizeUtcNow(utcNow);
            DateTime candidate;
            try
            {
                candidate = parsed.Kind == DateTimeKind.Utc
                    ? parsed
                    : parsed.ToUniversalTime();
            }
            catch (ArgumentException)
            {
                return safeNow.ToLocalTime();
            }

            DateTime latest = safeNow <= DateTime.MaxValue - MaximumFutureSkew
                ? safeNow + MaximumFutureSkew
                : DateTime.MaxValue;
            if (candidate < DateTime.UnixEpoch || candidate > latest)
            {
                candidate = safeNow;
            }

            return candidate.ToLocalTime();
        }

        private static DateTime NormalizeUtcNow(DateTime utcNow)
        {
            DateTime normalized;
            try
            {
                normalized = utcNow.Kind == DateTimeKind.Utc
                    ? utcNow
                    : utcNow.ToUniversalTime();
            }
            catch (ArgumentException)
            {
                normalized = DateTime.UtcNow;
            }

            if (normalized < DateTime.UnixEpoch)
            {
                return DateTime.UnixEpoch;
            }

            return DateTime.SpecifyKind(normalized, DateTimeKind.Utc);
        }
    }
}
