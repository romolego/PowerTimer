using System;
using System.Diagnostics;
using System.Globalization;

namespace PowerTimer {
    public interface IClock { DateTime UtcNow { get; } double Elapsed { get; } }
    public sealed class SystemClock : IClock {
        readonly Stopwatch watch = Stopwatch.StartNew();
        public DateTime UtcNow { get { return DateTime.UtcNow; } }
        public double Elapsed { get { return watch.Elapsed.TotalSeconds; } }
    }
    public sealed class TimerRecord {
        public string state { get; set; }
        public string deadlineUtc { get; set; }
        public double remainingSeconds { get; set; }
        public double totalSeconds { get; set; }
    }
    public sealed class TimerEngine {
        public const int MaximumSeconds = 604800;
        public const string ShutdownAction = "shutdown";
        public const string HibernateAction = "hibernate";
        const string HibernateRecordPrefix = "hibernate-";
        readonly IClock clock;
        readonly Action<string> executePowerAction;
        double due, frozen, total;
        long generation;
        public string State { get; private set; }
        public string ScheduledAction { get; private set; }
        public string Message { get; private set; }
        public event Action Changed;
        public TimerEngine(IClock clock, Action<string> executePowerAction) {
            this.clock = clock;
            this.executePowerAction = executePowerAction;
            State = "idle";
            ScheduledAction = ShutdownAction;
            Message = "Таймер не запущен";
        }
        public bool Active { get { return State == "running" || State == "paused"; } }
        public double Remaining { get { return State == "running" ? Math.Max(0, due - clock.Elapsed) : State == "paused" ? frozen : 0; } }
        public double Total { get { return total; } }
        public DateTime? Deadline { get { return State == "running" ? (DateTime?)clock.UtcNow.AddSeconds(Remaining) : null; } }
        public static string Format(double seconds) {
            long n = (long)Math.Ceiling(Math.Max(0, seconds));
            return String.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", n / 3600, n / 60 % 60, n % 60);
        }
        public static void Validate(double seconds) {
            if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds < 1 || seconds > MaximumSeconds)
                throw new ArgumentException("Укажите время от 1 секунды до 7 суток.");
        }
        void Notify() { if (Changed != null) Changed(); }
        public void Start(double seconds, string action = ShutdownAction) {
            Validate(seconds);
            if (action != ShutdownAction && action != HibernateAction)
                throw new ArgumentException("Неизвестное действие таймера.");
            generation++; total = seconds; frozen = 0; due = clock.Elapsed + seconds;
            ScheduledAction = action;
            State = "running"; Message = "Таймер запущен"; Notify();
        }
        public void Cancel() {
            generation++; State = "idle"; frozen = total = 0;
            Message = ScheduledAction == HibernateAction ? "Гибернация отменена" : "Выключение отменено";
            Notify();
        }
        public void StopWithMessage(string message) { generation++; State = "idle"; frozen = total = 0; Message = message; Notify(); }
        public void Pause() {
            if (State != "running") throw new InvalidOperationException("Нет работающего таймера.");
            frozen = Remaining; State = "paused";
            Message = ScheduledAction == HibernateAction ? "Отсчёт на паузе — гибернация не запланирована" : "Отсчёт на паузе — выключение не запланировано";
            Notify();
        }
        public void Resume() {
            if (State != "paused") throw new InvalidOperationException("Таймер не на паузе.");
            due = clock.Elapsed + frozen; State = "running"; Message = "Таймер продолжен"; Notify();
        }
        public void Extend(double seconds) {
            if (!Active) throw new InvalidOperationException("Сначала запустите таймер.");
            Validate(seconds); double next = Remaining + seconds; Validate(next);
            if (State == "running") due = clock.Elapsed + next; else frozen = next;
            total = next; Message = "Время добавлено"; Notify();
        }
        public void Tick() {
            if (State != "running" || Remaining > 0) return;
            long completedGeneration = generation;
            string action = ScheduledAction;
            bool hibernate = action == HibernateAction;
            State = "completed"; total = frozen = 0;
            Message = hibernate ? "Запрошена гибернация Windows" : "Запрошено выключение Windows";
            // Clear the persisted timer before the one-shot power request. Never retry on failure.
            Notify();
            // A callback may cancel or replace this timer while handling the state change.
            // That newer decision must win before any irreversible power request.
            if (generation != completedGeneration || State != "completed") return;
            try { executePowerAction(action); }
            catch (Exception ex) {
                State = "error";
                Message = (hibernate ? "Windows не приняла запрос гибернации: " : "Windows не приняла выключение: ") + ex.Message;
                Notify();
            }
        }
        public TimerRecord Export() {
            double remaining = Remaining;
            // Older builds must discard an active hibernation record instead of restoring it as shutdown.
            string savedState = Active && ScheduledAction == HibernateAction ? HibernateRecordPrefix + State : State;
            return new TimerRecord { state = savedState, deadlineUtc = State == "running" ? clock.UtcNow.AddSeconds(remaining).ToString("o") : null,
                remainingSeconds = remaining, totalSeconds = total };
        }
        public void Restore(TimerRecord record) {
            generation++; State = "idle"; frozen = total = 0; Message = "Таймер не запущен";
            ScheduledAction = ShutdownAction;
            if (record == null || record.state == null) return;
            bool hibernate = record.state.StartsWith(HibernateRecordPrefix, StringComparison.Ordinal);
            string savedState = hibernate ? record.state.Substring(HibernateRecordPrefix.Length) : record.state;
            if (savedState != "running" && savedState != "paused") return;
            double left = record.remainingSeconds;
            if (savedState == "running") {
                DateTime utc;
                if (!DateTime.TryParse(record.deadlineUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out utc)) return;
                left = (utc.ToUniversalTime() - clock.UtcNow).TotalSeconds;
            }
            if (Double.IsNaN(left) || Double.IsInfinity(left) || left <= 0 || left > MaximumSeconds) {
                Message = "Старый таймер истёк и снят. Задайте новый."; return;
            }
            // After a restart or crash require a deliberate resume, rather than a surprise shutdown.
            double savedTotal = record.totalSeconds;
            if (Double.IsNaN(savedTotal) || Double.IsInfinity(savedTotal) || savedTotal < left) savedTotal = left;
            frozen = left; total = Math.Max(left, Math.Min(MaximumSeconds, savedTotal));
            ScheduledAction = hibernate ? HibernateAction : ShutdownAction;
            State = "paused"; Message = "Таймер восстановлен на паузе. Нажмите «Продолжить».";
        }
    }
}
