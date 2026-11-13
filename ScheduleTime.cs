using System;
using System.Collections.Generic;
using System.Globalization;

namespace PowerTimer {
    static class ScheduleTime {
        static readonly string[] ClockFormats = { @"h\:mm", @"hh\:mm" };
        static readonly string[] TimestampFormats = {
            "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"
        };

        static DateTime Minute(DateTime value) {
            return value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMinute));
        }

        public static DateTime InitialLocal(DateTimeOffset now, string option, TimeZoneInfo zone) {
            if (option == "now") return Minute(TimeZoneInfo.ConvertTime(now, zone).DateTime);
            DateTime initial = Minute(TimeZoneInfo.ConvertTime(now.AddHours(1), zone).DateTime);
            bool fullHour = option == "hour";
            if (fullHour && initial.Minute != 0) initial = initial.AddMinutes(60 - initial.Minute);
            while (zone.IsInvalidTime(initial) || zone.IsAmbiguousTime(initial))
                initial = initial.AddMinutes(fullHour ? 60 : 1);
            return initial;
        }

        public static DateTimeOffset Limit(DateTimeOffset now) { return now.AddSeconds(TimerEngine.MaximumSeconds); }

        public static bool InWindow(DateTimeOffset target, DateTimeOffset now) {
            return target > now && target <= Limit(now);
        }

        public static DateTime BoundaryDate(DateTimeOffset now, TimeZoneInfo zone, bool last) {
            DateTime utc = Minute((last ? Limit(now) : now).UtcDateTime);
            if (!last) utc = utc.AddMinutes(1);
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
            while (zone.IsAmbiguousTime(local)) {
                utc = utc.AddMinutes(last ? -1 : 1);
                local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
            }
            return local.Date;
        }

        public static List<string> Options(DateTime? date, DateTimeOffset now, TimeZoneInfo zone, int step) {
            DateTime localNow = TimeZoneInfo.ConvertTime(now, zone).DateTime;
            DateTime day = (date ?? localNow).Date;
            DateTimeOffset limit = Limit(now);
            var options = new List<string>();
            for (int minute = 0; minute < 1440; minute += step) {
                DateTime local = day.AddMinutes(minute);
                if (!date.HasValue && local <= localNow) local = local.AddDays(1);
                if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local)) continue;
                var target = new DateTimeOffset(local, zone.GetUtcOffset(local));
                if (target > now && target <= limit)
                    options.Add(local.ToString("HH:mm", CultureInfo.InvariantCulture));
            }
            return options;
        }

        public static DateTimeOffset Resolve(string time, DateTime? date, DateTimeOffset now, TimeZoneInfo zone) {
            TimeSpan value;
            if (!TimeSpan.TryParseExact((time ?? "").Trim(), ClockFormats, CultureInfo.InvariantCulture, out value) || value.TotalHours >= 24)
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
