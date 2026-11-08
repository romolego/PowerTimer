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
            prefs.defaultSeconds = -1; prefs.widgetX = Double.PositiveInfinity; prefs.theme = "invalid";
            prefs.Normalize();
            Check(prefs.presetMinutes.Length == 7 && prefs.presetMinutes[6] == 240, "legacy preset migration");
            Check(prefs.defaultSeconds == 7200 && prefs.widgetX == null && prefs.theme == "dark", "damaged settings normalized");
            Reject(delegate { Preferences.Patch(prefs, new Dictionary<string, object> { { "defaultSeconds", 10800 }, { "widgetOpacity", 5 } }); }, "atomic preference validation");
            Check(prefs.defaultSeconds == 7200 && prefs.widgetOpacity == 100, "failed patch does not mutate original preferences");
            Reject(delegate { Preferences.Patch(prefs, new Dictionary<string, object> { { "defaultSeconds", true } }); }, "boolean is not duration");
            Reject(delegate { Preferences.Patch(prefs, new Dictionary<string, object> { { "theme", null } }); }, "null theme rejected");

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
                prefs.defaultSeconds = 10800;
                bool rejected = false;
                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    try { Store.WriteChanged(path, prefs, ref previous); }
                    catch (IOException) { rejected = true; }
                }
                Check(rejected && File.ReadAllText(path) == saved && previous == saved, "failed atomic replacement preserves old file and cache");
                Check(!File.Exists(path + ".tmp"), "failed save cleans temporary file");
                Store.WriteChanged(path, prefs, ref previous);
                Check(Store.Read<Preferences>(path).defaultSeconds == 10800, "save retries after storage error");
                File.WriteAllText(path, "{broken");
                Check(Store.Read<Preferences>(path) == null, "broken JSON falls back safely");
                File.WriteAllText(path, new string('x', 65537));
                Check(Store.Read<Preferences>(path) == null, "oversized settings file rejected");
            } finally { Directory.Delete(folder, true); }
        }
    }
}
