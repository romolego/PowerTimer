using System;
using System.Collections.Generic;
using System.IO;

namespace PowerTimer {
    public static partial class CoreTests {
        static void ReliabilityChecks() {
            Check(DurationInput.Parse("3,5", true) == 12600, "comma decimal hours");
            Check(DurationInput.Parse("3.5", true) == 12600, "dot decimal hours");
            Check(DurationInput.Parse("0,5", true) == 1800, "fractional hour below one");
            Check(DurationInput.Parse("1,5", false) == 90, "fractional minutes");
            Check(DurationInput.Parse("168", true) == 604800, "maximum custom hours");
            Check(DurationInput.Parse("10080", false) == 604800, "maximum custom minutes");
            Check(DurationInput.Parse(" 2,25 ", true) == 8100, "custom whitespace");
            foreach (string text in new[] { "", "NaN", "Infinity", "1e3", "1,2.3", "-2", "0", "0.5", "10080.1", "999999999999999999999999999999999999" })
                Reject(delegate { DurationInput.Parse(text, false); }, "invalid custom duration " + text);
            Check(DurationInput.Short(12600) == "3 ч 30 мин", "human readable fractional hours");
            Check(DurationInput.Short(90) == "00:01:30", "seconds are not truncated in selection label");

            var prefs = new Preferences();
            prefs.presetMinutes = new[] { 10, 15, 30, 60, 120, 180, 240, 300 };
            prefs.defaultSeconds = -1; prefs.widgetX = Double.PositiveInfinity; prefs.theme = "invalid"; prefs.defaultAction = "sleep";
            prefs.Normalize();
            Check(String.Join(",", prefs.presetMinutes) == "120,10,15,30,60,180,240", "legacy default moves to first preset without losing the other six");
            Check(prefs.defaultSeconds == 7200 && prefs.widgetX == null && prefs.theme == "dark" && prefs.defaultAction == TimerEngine.ShutdownAction, "damaged settings normalized");
            Reject(delegate { Preferences.Patch(prefs, new Dictionary<string, object> { { "defaultSeconds", 10800 }, { "widgetOpacity", 5 } }); }, "atomic preference validation");
            Check(prefs.defaultSeconds == 7200 && prefs.widgetOpacity == 100, "failed patch does not mutate original preferences");
            Reject(delegate { Preferences.Patch(prefs, new Dictionary<string, object> { { "defaultSeconds", true } }); }, "boolean is not duration");
            Reject(delegate { Preferences.Patch(prefs, new Dictionary<string, object> { { "defaultAction", "sleep" } }); }, "unsupported default action rejected");
            Reject(delegate { Preferences.Patch(prefs, new Dictionary<string, object> { { "theme", null } }); }, "null theme rejected");
            var patched = Preferences.Patch(prefs, new Dictionary<string, object> {
                { "defaultSeconds", 3600 }, { "presetMinutes", new[] { 15, 30, 60, 120, 180, 240, 300 } }
            });
            Check(patched.defaultSeconds == 3600 && String.Join(",", patched.presetMinutes) == "60,15,30,120,180,240,300",
                  "legacy defaultSeconds patch moves the requested preset first");

            string folder = Path.Combine(Path.GetTempPath(), "PowerTimer.Tests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try {
                string path = Path.Combine(folder, "settings.json"), previous = null;
                Store.WriteChanged(path, prefs, ref previous);
                Check(Store.Read<Preferences>(path).defaultSeconds == 7200, "settings round trip");
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                Store.WriteChanged(path, prefs, ref previous);
                Check(File.GetLastWriteTimeUtc(path) == stamp, "unchanged preferences do not touch disk");
                string saved = File.ReadAllText(path);
                prefs.presetMinutes[0] = 180;
                prefs.defaultAction = TimerEngine.HibernateAction;
                bool rejected = false;
                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    try { Store.WriteChanged(path, prefs, ref previous); }
                    catch (IOException) { rejected = true; }
                }
                Check(rejected && File.ReadAllText(path) == saved && previous == saved, "failed atomic replacement preserves old file and cache");
                Check(!File.Exists(path + ".tmp"), "failed save cleans temporary file");
                Store.WriteChanged(path, prefs, ref previous);
                var reloaded = Store.Read<Preferences>(path);
                reloaded.Normalize();
                Check(reloaded.defaultSeconds == 10800 && reloaded.defaultAction == TimerEngine.HibernateAction, "first preset and default action survive a save retry and reload");
                File.WriteAllText(path, "{broken");
                Check(Store.Read<Preferences>(path) == null, "broken JSON falls back safely");
                File.WriteAllText(path, new string('x', 65537));
                Check(Store.Read<Preferences>(path) == null, "oversized settings file rejected");
                File.WriteAllText(path, "{\"schemaVersion\":2,\"defaultSeconds\":4500,\"presetMinutes\":[10,15,30,60,120,180,240]}");
                var migrated = Store.Read<Preferences>(path);
                migrated.Normalize();
                Check(String.Join(",", migrated.presetMinutes) == "75,10,15,30,60,120,180", "custom legacy default is inserted before the first six presets");
            } finally { Directory.Delete(folder, true); }
        }
    }
}
