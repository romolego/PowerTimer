using System;
using System.Globalization;

namespace PowerTimer {
    static class ScheduleTime {
        static readonly string[] ClockFormats = { @"h\:mm", @"hh\:mm" };
        static readonly string[] TimestampFormats = {
            "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"
        };

        public static DateTime InitialLocal(DateTime now, bool nextHour) {
            var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
            var initial = nextHour ? minute.AddMinutes(60 - now.Minute) : minute.AddHours(1);
            var zone = TimeZoneInfo.Local;
            while (zone.IsInvalidTime(initial) || zone.IsAmbiguousTime(initial))
                initial = initial.AddMinutes(1);
            return initial;
        }

        public static DateTimeOffset Resolve(string time, DateTime? date, DateTimeOffset now, TimeZoneInfo zone) {
            TimeSpan value;
            if (!TimeSpan.TryParseExact(time, ClockFormats, CultureInfo.InvariantCulture, out value) || value.TotalHours >= 24)
                throw new ArgumentException("Введите время в формате ЧЧ:ММ, например 23:30.");
            DateTime localNow = TimeZoneInfo.ConvertTime(now, zone).DateTime;
            DateTime local = DateTime.SpecifyKind((date ?? localNow).Date.Add(value), DateTimeKind.Unspecified);
            if (!date.HasValue && local <= localNow) local = local.AddDays(1);
            if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
                throw new ArgumentException("Выберите другое время. В этот час переводят часы.");
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }

        public static DateTimeOffset ParseTimestamp(string text) {
            DateTimeOffset result;
            bool hasOffset = text != null && (text.EndsWith("Z", StringComparison.Ordinal) ||
                (text.Length >= 6 && (text[text.Length - 6] == '+' || text[text.Length - 6] == '-')));
            if (!hasOffset || !DateTimeOffset.TryParseExact(text, TimestampFormats, CultureInfo.InvariantCulture,
                                                           DateTimeStyles.None, out result))
                throw new ArgumentException("Укажите дату и время ISO 8601 с часовым поясом, например 2026-11-14T23:30:00+07:00.");
            return result;
        }
    }
}
