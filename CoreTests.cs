using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PowerTimer {
    sealed class FakeClock : IClock {
        public DateTime UtcNow { get; set; }
        public double Elapsed { get; set; }
        public FakeClock() { UtcNow = new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc); }
        public void Advance(double n) { Elapsed += n; UtcNow = UtcNow.AddSeconds(n); }
    }
    public static partial class CoreTests {
        static int count;
        static void Check(bool value, string name) { count++; if (!value) throw new Exception("FAIL: " + name); }
        static void Reject(Action action, string name) {
            bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, name);
        }
        static string ApiRequest(int port, string method, string route, string body, string authorization, string extraHeaders) {
            using (var client = new TcpClient()) {
                client.Connect("127.0.0.1", port); client.ReceiveTimeout = 3000;
                byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
                string header = method + " " + route + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nContent-Type: application/json\r\nContent-Length: " + payload.Length + "\r\n";
                if (authorization != null) header += "Authorization: Bearer " + authorization + "\r\n";
                header += (extraHeaders ?? "") + "\r\n";
                byte[] head = Encoding.ASCII.GetBytes(header);
                var stream = client.GetStream(); stream.Write(head, 0, head.Length); stream.Write(payload, 0, payload.Length);
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }
        static void ApiChecks() {
            int dispatched = 0;
            using (var api = new LocalApi("test-token", delegate(string method, string path, Dictionary<string, object> body) {
                Interlocked.Increment(ref dispatched);
                if (path == "/io-error") throw new IOException("test storage failure");
                return new { accepted = true };
            }, 0)) {
                string result = ApiRequest(api.Port, "POST", "/timer", "{\"seconds\":60}", "test-token", null);
                Check(result.StartsWith("HTTP/1.1 200 ") && result.EndsWith("{\"accepted\":true}"), "API full success response");
                Check(dispatched == 1, "API handler once");
                result = ApiRequest(api.Port, "POST", "/timer", "{\"seconds\":60}", null, null);
                Check(result.StartsWith("HTTP/1.1 401 ") && result.Contains("Bearer token required"), "API error body survives rejected request body");
                Check(dispatched == 1, "unauthenticated request never dispatched");
                result = ApiRequest(api.Port, "POST", "/timer", "{\"seconds\":60}", "test-token", "Origin: http://untrusted.example\r\n");
                Check(result.StartsWith("HTTP/1.1 403 ") && dispatched == 1, "API browser origin rejected");
                result = ApiRequest(api.Port, "POST", "/timer", "{", "test-token", null);
                Check(result.StartsWith("HTTP/1.1 400 ") && dispatched == 1, "malformed JSON rejected before dispatch");
                result = ApiRequest(api.Port, "POST", "/timer", "{}", "test-token", "Content-Length: 2\r\n");
                Check(result.StartsWith("HTTP/1.1 400 ") && dispatched == 1, "duplicate length rejected");
                result = ApiRequest(api.Port, "GET", "/io-error", null, "test-token", null);
                Check(result.StartsWith("HTTP/1.1 500 ") && result.Contains("I/O operation failed"), "storage failure returns JSON error");
                // A connection waiting for a body is closed by Dispose, instead of retaining
                // its worker and eventually executing against a stopped application.
                using (var pending = new TcpClient()) {
                    pending.Connect("127.0.0.1", api.Port); pending.ReceiveTimeout = 3000;
                    var stream = pending.GetStream();
                    byte[] head = Encoding.ASCII.GetBytes("POST /timer HTTP/1.1\r\nHost: 127.0.0.1:" + api.Port + "\r\nAuthorization: Bearer test-token\r\nContent-Type: application/json\r\nContent-Length: 20\r\n\r\n");
                    stream.Write(head, 0, head.Length); Thread.Sleep(80); int before = dispatched;
                    api.Dispose();
                    bool closed = false; try { closed = stream.ReadByte() == -1; } catch (IOException) { closed = true; }
                    Check(closed && dispatched == before, "API disposal closes pending connection without dispatch");
                    Thread.Sleep(120);
                }
            }
            using (var api = new LocalApi("test-token", delegate(string method, string path, Dictionary<string, object> body) {
                Interlocked.Increment(ref dispatched); return new { accepted = true };
            }, 0)) {
                int before = dispatched;
                var occupied = new List<TcpClient>();
                try {
                    for (int i = 0; i < 8; i++) {
                        var client = new TcpClient(); client.Connect("127.0.0.1", api.Port);
                        client.GetStream().WriteByte((byte)'G'); occupied.Add(client);
                    }
                    Thread.Sleep(80);
                    string busy = ApiRequest(api.Port, "GET", "/status", null, "test-token", null);
                    Check(busy.StartsWith("HTTP/1.1 503 ") && dispatched == before, "connection limit returns retryable error");
                } finally { foreach (var client in occupied) client.Close(); }
            }
            using (var api = new LocalApi("test-token", delegate(string method, string path, Dictionary<string, object> body) {
                Interlocked.Increment(ref dispatched); return new { accepted = true };
            }, 0)) {
                int before = dispatched;
                using (var slow = new TcpClient()) {
                    slow.Connect("127.0.0.1", api.Port); slow.ReceiveTimeout = 3000;
                    var stream = slow.GetStream();
                    byte[] header = Encoding.ASCII.GetBytes("POST /timer HTTP/1.1\r\nHost: 127.0.0.1:" + api.Port + "\r\nAuthorization: Bearer test-token\r\nContent-Type: application/json\r\nContent-Length: 20\r\n\r\n");
                    stream.Write(header, 0, header.Length);
                    // Activity every second would evade a per-read timeout, but must not
                    // reset the total five-second request deadline.
                    for (int i = 0; i < 4; i++) { Thread.Sleep(1000); stream.WriteByte((byte)' '); }
                    Thread.Sleep(1200);
                    string timeout; using (var reader = new StreamReader(stream, Encoding.UTF8)) timeout = reader.ReadToEnd();
                    Check(timeout.StartsWith("HTTP/1.1 408 ") && dispatched == before, "total request deadline is not reset by partial body chunks");
                }
            }
        }
        public static int Main() {
            try {
                int powered = 0; var clock = new FakeClock(); var timer = new TimerEngine(clock, delegate { powered++; });
                Check(timer.State == "idle" && !timer.Active, "initial idle");
                Reject(delegate { timer.Start(0); }, "reject zero"); Reject(delegate { timer.Start(-1); }, "reject negative");
                Reject(delegate { timer.Start(Double.NaN); }, "reject NaN"); Reject(delegate { timer.Start(Double.PositiveInfinity); }, "reject infinity");
                Reject(delegate { timer.Start(604801); }, "reject over max");
                timer.Start(600); clock.Advance(30.2);
                Check(TimerEngine.Format(timer.Remaining) == "00:09:30", "countdown rounding");
                clock.UtcNow = clock.UtcNow.AddHours(4); Check(Math.Abs(timer.Remaining - 569.8) < .001, "clock changes do not shorten countdown");
                timer.Pause(); double frozen = timer.Remaining; clock.Advance(1000);
                Check(timer.State == "paused" && timer.Remaining == frozen && timer.Deadline == null, "pause freezes deadline");
                timer.Extend(600); Check(Math.Abs(timer.Remaining - frozen - 600) < .001, "extend paused");
                timer.Resume(); clock.Advance(1); Check(timer.Remaining < frozen + 600, "resume");
                timer.Start(10); clock.Advance(11); timer.Tick(); timer.Tick();
                Check(powered == 1 && timer.State == "completed", "due once only");
                timer.Start(1); timer.Cancel(); clock.Advance(2); timer.Tick(); Check(powered == 1, "cancel suppresses power action");
                timer.Start(60); timer.Start(120); clock.Advance(61); timer.Tick(); Check(powered == 1 && timer.Active, "replacement timer supersedes old deadline");
                var record = timer.Export(); var restored = new TimerEngine(clock, delegate { powered++; }); restored.Restore(record);
                Check(restored.State == "paused" && restored.Remaining > 58, "restore on pause");
                record.deadlineUtc = clock.UtcNow.AddSeconds(-1).ToString("o"); restored = new TimerEngine(clock, delegate { powered++; }); restored.Restore(record);
                Check(!restored.Active && powered == 1, "expired timer discarded");
                timer.Start(604800); Reject(delegate { timer.Extend(1); }, "extend max bound");
                timer.Cancel(); Reject(timer.Pause, "pause idle rejected"); Reject(timer.Resume, "resume idle rejected"); Reject(delegate { timer.Extend(10); }, "extend idle rejected");
                var failing = new TimerEngine(clock, delegate { throw new Exception("test error"); }); failing.Start(1); clock.Advance(2); failing.Tick();
                Check(failing.State == "error" && !failing.Active, "power failure captured");
                Check(TimerEngine.Format(360000) == "100:00:00", "100 hour formatting");
                Check(ControlCli.ParseDuration("2h") == 7200 && ControlCli.ParseDuration("1.5m") == 90, "CLI units");
                Reject(delegate { ControlCli.ParseDuration("NaN"); }, "CLI NaN");
                restored.Restore(new TimerRecord { state = "paused", remainingSeconds = 60, totalSeconds = Double.NaN });
                Check(restored.State == "paused" && restored.Total == 60, "corrupt total does not propagate NaN to UI");
                clock.Advance(3600); Check(restored.Remaining == 60, "restored paused timer does not age");
                restored.Restore(new TimerRecord { state = "paused", remainingSeconds = Double.NaN, totalSeconds = 60 });
                Check(restored.State == "idle" && !restored.Active, "invalid restore clears previous timer");
                restored.Restore(new TimerRecord { state = "running", deadlineUtc = clock.UtcNow.AddDays(8).ToString("o"), totalSeconds = 60 });
                Check(!restored.Active, "restore refuses deadline beyond maximum");
                restored.Restore(new TimerRecord { state = "running", deadlineUtc = "not-a-date", totalSeconds = 60 });
                Check(!restored.Active, "restore invalid deadline");
                var cancelDuringNotification = new TimerEngine(clock, delegate { powered++; });
                cancelDuringNotification.Changed += delegate { if (cancelDuringNotification.State == "completed") cancelDuringNotification.Cancel(); };
                int beforePower = powered; cancelDuringNotification.Start(1); clock.Advance(2); cancelDuringNotification.Tick();
                Check(powered == beforePower && cancelDuringNotification.State == "idle", "cancellation in completion callback wins before power request");
                var replaceDuringNotification = new TimerEngine(clock, delegate { powered++; });
                replaceDuringNotification.Changed += delegate { if (replaceDuringNotification.State == "completed") replaceDuringNotification.Start(600); };
                replaceDuringNotification.Start(1); clock.Advance(2); replaceDuringNotification.Tick();
                Check(powered == beforePower && replaceDuringNotification.State == "running" && replaceDuringNotification.Remaining == 600, "replacement in completion callback supersedes finished timer");
                ReliabilityChecks();
                ApiChecks();
                Console.WriteLine("PASS: " + count + " core and transport checks (fake clock and power backend). No system power calls."); return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
