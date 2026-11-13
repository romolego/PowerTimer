using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace PowerTimer {
    public sealed class Preferences {
        const int CurrentSchemaVersion = 3;
        const int InitialPresetMinutes = 120;
        int legacyDefaultSeconds;
        bool legacyScheduleNextHour;
        string initialTime;

        public int schemaVersion { get; set; }
        public string theme { get; set; }
        public string defaultAction { get; set; }
        public string defaultTimingMode { get; set; }
        public bool scheduleTimeOnly { get; set; }
        public string scheduleInitialTime {
            get { return initialTime ?? (legacyScheduleNextHour ? "hour" : "now"); }
            set { initialTime = value; }
        }
        public bool scheduleNextHour {
            get { return scheduleInitialTime == "hour"; }
            set { legacyScheduleNextHour = value; }
        }
        // Keep the old API field derived from the first preset. Its setter reads old files.
        public int defaultSeconds {
            get { return presetMinutes[0] * 60; }
            set { legacyDefaultSeconds = value; }
        }
        public int addSeconds { get; set; }
        public int[] presetMinutes { get; set; }
        public bool keepAwake { get; set; }
        public bool closeToTray { get; set; }
        public bool minimizeToTray { get; set; }
        public bool startWithWindows { get; set; }
        public bool startInTray { get; set; }
        public bool notifyBeforeShutdown { get; set; }
        public bool widgetTopmost { get; set; }
        public string widgetSize { get; set; }
        public int widgetOpacity { get; set; }
        public bool rememberWidgetPosition { get; set; }
        public bool widgetMode { get; set; }
        public double? widgetX { get; set; }
        public double? widgetY { get; set; }
        public Preferences() {
            theme = "dark"; defaultAction = TimerEngine.ShutdownAction; defaultTimingMode = "timer"; addSeconds = 600;
            presetMinutes = new int[] { InitialPresetMinutes, 10, 15, 30, 60, 180, 240 };
            keepAwake = closeToTray = minimizeToTray = startInTray = notifyBeforeShutdown = true;
            widgetTopmost = rememberWidgetPosition = true; widgetSize = "small"; widgetOpacity = 100;
        }
        public Preferences Clone() {
            var copy = (Preferences)MemberwiseClone(); copy.presetMinutes = (int[])presetMinutes.Clone(); return copy;
        }
        static bool ValidDuration(int n) { return n >= 60 && n <= TimerEngine.MaximumSeconds && n % 60 == 0; }
        static bool ValidInitialTime(string value) { return value == "now" || value == "later" || value == "hour"; }
        void PutPresetFirst(int minutes) {
            int index = Array.IndexOf(presetMinutes, minutes);
            if (index < 0) index = presetMinutes.Length - 1;
            Array.Copy(presetMinutes, 0, presetMinutes, 1, index);
            presetMinutes[0] = minutes;
        }
        public void Normalize() {
            if (theme != "dark" && theme != "light" && theme != "system") theme = "dark";
            if (!TimerEngine.IsSupportedAction(defaultAction)) defaultAction = TimerEngine.ShutdownAction;
            if (defaultTimingMode != "timer" && defaultTimingMode != "schedule") defaultTimingMode = "timer";
            if (!ValidInitialTime(scheduleInitialTime)) scheduleInitialTime = "now";
            if (!ValidDuration(addSeconds)) addSeconds = 600;
            if (presetMinutes != null && presetMinutes.Length == 8) presetMinutes = new List<int>(presetMinutes).GetRange(0, 7).ToArray();
            bool valid = presetMinutes != null && presetMinutes.Length == 7;
            if (valid) foreach (int value in presetMinutes) valid &= value >= 1 && value <= 10080;
            if (!valid) presetMinutes = new Preferences().presetMinutes;
            if (schemaVersion < CurrentSchemaVersion) {
                int minutes = ValidDuration(legacyDefaultSeconds) ? legacyDefaultSeconds / 60 : InitialPresetMinutes;
                PutPresetFirst(minutes);
            }
            schemaVersion = CurrentSchemaVersion;
            if (widgetSize != "small" && widgetSize != "medium" && widgetSize != "large") widgetSize = "small";
            if (widgetOpacity < 60 || widgetOpacity > 100) widgetOpacity = 100;
            if (widgetX.HasValue && (Double.IsNaN(widgetX.Value) || Double.IsInfinity(widgetX.Value))) widgetX = null;
            if (widgetY.HasValue && (Double.IsNaN(widgetY.Value) || Double.IsInfinity(widgetY.Value))) widgetY = null;
            if (widgetX == -1 && widgetY == -1) { widgetX = null; widgetY = null; }
        }
        public void Validate() {
            if (theme != "dark" && theme != "light" && theme != "system") throw new ArgumentException("Неизвестная тема.");
            if (!TimerEngine.IsSupportedAction(defaultAction)) throw new ArgumentException("Выберите выключение или гибернацию.");
            if (defaultTimingMode != "timer" && defaultTimingMode != "schedule") throw new ArgumentException("Выберите режим по умолчанию.");
            if (!ValidInitialTime(scheduleInitialTime)) throw new ArgumentException("Выберите начальное время.");
            if (!ValidDuration(addSeconds)) throw new ArgumentException("Шаг добавления: от 1 минуты до 7 суток.");
            if (presetMinutes == null || presetMinutes.Length != 7) throw new ArgumentException("Нужно ровно 7 интервалов.");
            foreach (int minutes in presetMinutes) if (minutes < 1 || minutes > 10080) throw new ArgumentException("Каждый интервал: от 1 до 10080 минут.");
            if (widgetSize != "small" && widgetSize != "medium" && widgetSize != "large") throw new ArgumentException("Неизвестный размер виджета.");
            if (widgetOpacity < 60 || widgetOpacity > 100) throw new ArgumentException("Прозрачность: от 60 до 100%.");
        }
        public static int Integer(object value, string key) {
            if (!(value is int || value is long || value is decimal || value is double)) throw new ArgumentException(key + ": ожидается целое число.");
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (Double.IsNaN(number) || Double.IsInfinity(number) || number != Math.Truncate(number) || number < Int32.MinValue || number > Int32.MaxValue)
                throw new ArgumentException(key + ": ожидается целое число.");
            return (int)number;
        }
        static bool Boolean(object value, string key) {
            if (!(value is bool)) throw new ArgumentException(key + ": ожидается true или false."); return (bool)value;
        }
        static string Text(object value, string key) {
            if (!(value is string)) throw new ArgumentException(key + ": ожидается строка."); return (string)value;
        }
        public static Preferences Patch(Preferences current, Dictionary<string, object> data) {
            Preferences next = current.Clone();
            int? requestedDefaultSeconds = null;
            bool? requestedNextHour = null;
            foreach (var pair in data) {
                string key = pair.Key; object value = pair.Value;
                switch (key) {
                    case "theme": next.theme = Text(value, key); break;
                    case "defaultAction": next.defaultAction = Text(value, key); break;
                    case "defaultTimingMode": next.defaultTimingMode = Text(value, key); break;
                    case "scheduleTimeOnly": next.scheduleTimeOnly = Boolean(value, key); break;
                    case "scheduleInitialTime": next.scheduleInitialTime = Text(value, key); break;
                    case "scheduleNextHour": requestedNextHour = Boolean(value, key); break;
                    case "defaultSeconds":
                        int seconds = Integer(value, key);
                        if (!ValidDuration(seconds)) throw new ArgumentException("Интервал по умолчанию должен быть от 1 минуты до 7 суток.");
                        requestedDefaultSeconds = seconds;
                        break;
                    case "addSeconds": next.addSeconds = Integer(value, key); break;
                    case "presetMinutes":
                        if (value is string || !(value is IEnumerable)) throw new ArgumentException("presetMinutes: ожидается массив из 7 чисел.");
                        var list = new List<int>(); foreach (object item in (IEnumerable)value) list.Add(Integer(item, key));
                        next.presetMinutes = list.ToArray(); break;
                    case "keepAwake": next.keepAwake = Boolean(value, key); break;
                    case "closeToTray": next.closeToTray = Boolean(value, key); break;
                    case "minimizeToTray": next.minimizeToTray = Boolean(value, key); break;
                    case "startWithWindows": next.startWithWindows = Boolean(value, key); break;
                    case "startInTray": next.startInTray = Boolean(value, key); break;
                    case "notifyBeforeShutdown": next.notifyBeforeShutdown = Boolean(value, key); break;
                    case "widgetTopmost": next.widgetTopmost = Boolean(value, key); break;
                    case "widgetSize": next.widgetSize = Text(value, key); break;
                    case "widgetOpacity": next.widgetOpacity = Integer(value, key); break;
                    case "rememberWidgetPosition": next.rememberWidgetPosition = Boolean(value, key); break;
                    default: throw new ArgumentException("Неизвестный параметр: " + key);
                }
            }
            if (requestedNextHour.HasValue && !data.ContainsKey("scheduleInitialTime"))
                next.scheduleInitialTime = requestedNextHour.Value ? "hour" : "later";
            next.Validate();
            if (requestedDefaultSeconds.HasValue) next.PutPresetFirst(requestedDefaultSeconds.Value / 60);
            return next;
        }
    }
}
