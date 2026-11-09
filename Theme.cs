using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace PowerTimer {
    public static class Theme {
        public static string Resolve(string choice) {
            if (choice != "system") return choice;
            try {
                using (var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
                    return key != null && Convert.ToString(key.GetValue("AppsUseLightTheme", 1)) == "0" ? "dark" : "light";
            } catch { return "light"; }
        }
        public static string Apply(Application app, string choice) {
            string effective = Resolve(choice); bool dark = effective == "dark";
            string[] keys = { "WindowBg", "SurfaceBg", "RaisedBg", "BorderBrush", "TextBrush", "MutedBrush", "AccentBrush", "AccentTextBrush", "TrackBrush", "DangerBrush", "DangerBg", "ErrorBrush", "UrgentBrush", "DefaultPresetBg" };
            string[] colors = dark
                ? new[] { "#111B18", "#192A22", "#24372D", "#354C3E", "#EEF7F1", "#9CB6A6", "#B8F5CD", "#102B1B", "#3A5143", "#F7BFB6", "#3C2927", "#FFB8A7", "#FFC28B", "#304E3D" }
                : new[] { "#F6F8F5", "#FFFFFF", "#EDF2EC", "#CFDCD1", "#173023", "#526B5C", "#236E45", "#FFFFFF", "#CEDAD0", "#A23A30", "#FCEBE7", "#A13226", "#A15A13", "#D9EEE0" };
            for (int i = 0; i < keys.Length; i++) {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])); brush.Freeze(); app.Resources[keys[i]] = brush;
            }
            return effective;
        }
    }
}
