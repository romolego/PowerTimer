using System;
using System.Globalization;

namespace PowerTimer {
    public static class DurationInput {
        // Both decimal separators are accepted; grouping and scientific notation
        // are deliberately excluded so "3,5" always means three and a half.
        public static int Parse(string text, bool hours) {
            decimal value;
            if (text == null || !Decimal.TryParse(text.Trim().Replace(',', '.'),
                NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
                throw new ArgumentException("Введите число, например 3,5.");
            decimal multiplier = hours ? 3600m : 60m;
            if (value < 60m / multiplier || value > TimerEngine.MaximumSeconds / multiplier)
                throw new ArgumentException("От 1 минуты до 7 суток.");
            return (int)Decimal.Round(value * multiplier, 0, MidpointRounding.AwayFromZero);
        }

        public static string Short(int seconds) {
            if (seconds % 60 != 0) return TimerEngine.Format(seconds);
            int hours = seconds / 3600, minutes = seconds % 3600 / 60;
            if (hours == 0) return minutes + " мин";
            return hours + " ч" + (minutes == 0 ? "" : " " + minutes + " мин");
        }
    }
}
