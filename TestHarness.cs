using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerTimer {
    // The harness is reachable only from a --test process. It invokes the real
    // control handlers against the fake power backend, rather than duplicating UI logic.
    public sealed partial class TimerApplication {
        long testTickCount;
        void RequireTestHarness() {
            if (!test) throw new ApiError(404, "Unknown API route");
        }
        Window TestWindow(string name) {
            if (name == "custom") { EnsureCustom(); return custom; }
            if (name == "main") return main;
            if (name == "widget") return widget;
            if (name == "settings") { EnsureSettings(); return settings; }
            throw new ApiError(400, "window must be main, widget or settings");
        }
        static string TestString(Dictionary<string, object> data, string key) {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || !(value is string))
                throw new ApiError(400, key + " must be a string");
            return (string)value;
        }
        static object TestValue(Dictionary<string, object> data) {
            object value;
            if (!data.TryGetValue("value", out value)) throw new ApiError(400, "value is required");
            return value;
        }
        object TestInteraction(Dictionary<string, object> data) {
            RequireTestHarness();
            Window window = TestWindow(TestString(data, "window"));
            string action = TestString(data, "action");
            if (action == "stall") {
                int milliseconds = Preferences.Integer(TestValue(data), "value");
                if (milliseconds < 1 || milliseconds > 20000) throw new ApiError(400, "stall range is 1..20000 ms");
                System.Threading.Thread.Sleep(milliseconds); return Snapshot();
            }
            if (action == "exitRace") { OnUi(delegate { engine.Start(600); }); RequestExit(); return Snapshot(); }
            if (action == "move") {
                object x, y;
                if (!data.TryGetValue("x", out x) || !data.TryGetValue("y", out y)) throw new ApiError(400, "x and y required");
                double left = Convert.ToDouble(x), top = Convert.ToDouble(y);
                if (Double.IsNaN(left) || Double.IsInfinity(left) || Double.IsNaN(top) || Double.IsInfinity(top)) throw new ApiError(400, "Invalid coordinates");
                window.Left = left; window.Top = top;
                if (window == widget) { ClampWidget(); if (widgetExpanded) widgetCompactAnchor = new Point(widget.Left, widget.Top); SaveWidgetPosition(); Persist(); }
                return Snapshot();
            }
            if (action == "close") { window.Close(); return Snapshot(); }
            if (action == "minimize") { window.WindowState = WindowState.Minimized; return Snapshot(); }
            string name = TestString(data, "control");
            var control = window.FindName(name) as FrameworkElement;
            if (control == null) throw new ApiError(400, "Control was not found: " + name);
            if (!control.IsVisible) throw new ApiError(400, "Control is hidden: " + name);
            if (!control.IsEnabled) throw new ApiError(400, "Control is disabled: " + name);
            switch (action) {
                case "scroll": {
                    var scroll = control as ScrollViewer;
                    if (scroll == null) throw new ApiError(400, "scroll requires a ScrollViewer");
                    scroll.ScrollToVerticalOffset(Convert.ToDouble(TestValue(data)));
                    break;
                }
                case "click": {
                    var button = control as Button;
                    if (button == null) throw new ApiError(400, "click requires a Button");
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                    break;
                }
                case "text": {
                    var textBox = control as TextBox;
                    object value = TestValue(data);
                    if (textBox == null || !(value is string)) throw new ApiError(400, "text requires a TextBox and string value");
                    if (textBox.IsReadOnly) throw new ApiError(400, "TextBox is read-only: " + name);
                    textBox.Text = (string)value;
                    break;
                }
                case "check": {
                    var checkBox = control as CheckBox;
                    object value = TestValue(data);
                    if (checkBox == null || !(value is bool)) throw new ApiError(400, "check requires a CheckBox and boolean value");
                    checkBox.IsChecked = (bool)value;
                    checkBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, checkBox));
                    break;
                }
                case "select": {
                    var combo = control as ComboBox;
                    object value = TestValue(data);
                    if (combo == null || !(value is string)) throw new ApiError(400, "select requires a ComboBox and string tag value");
                    ComboBoxItem selected = null;
                    foreach (object item in combo.Items) {
                        var candidate = item as ComboBoxItem;
                        if (candidate != null && String.Equals(Convert.ToString(candidate.Tag, CultureInfo.InvariantCulture), (string)value, StringComparison.Ordinal)) {
                            selected = candidate; break;
                        }
                    }
                    if (selected == null || !selected.IsEnabled) throw new ApiError(400, "No enabled ComboBox item has that tag");
                    combo.SelectedItem = selected;
                    break;
                }
                case "slider": {
                    var slider = control as Slider;
                    object value = TestValue(data);
                    if (slider == null || !(value is int || value is long || value is double || value is decimal || value is float))
                        throw new ApiError(400, "slider requires a Slider and numeric value");
                    double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (Double.IsNaN(number) || Double.IsInfinity(number) || number < slider.Minimum || number > slider.Maximum)
                        throw new ApiError(400, "Slider value is outside its range");
                    slider.Value = number;
                    break;
                }
                default: throw new ApiError(400, "Unknown test interaction action");
            }
            window.UpdateLayout();
            return Snapshot();
        }
        static object InspectControlValue(FrameworkElement element) {
            var textBox = element as TextBox;
            if (textBox != null) return textBox.Text;
            var text = element as TextBlock;
            if (text != null) return text.Text;
            var checkBox = element as CheckBox;
            if (checkBox != null) return checkBox.IsChecked;
            var combo = element as ComboBox;
            if (combo != null) {
                var selected = combo.SelectedItem as ComboBoxItem;
                return selected == null ? null : Convert.ToString(selected.Tag, CultureInfo.InvariantCulture);
            }
            var slider = element as Slider;
            if (slider != null) return slider.Value;
            var scroll = element as ScrollViewer;
            if (scroll != null) return scroll.VerticalOffset;
            var button = element as Button;
            if (button != null) return Convert.ToString(button.Content, CultureInfo.InvariantCulture);
            return null;
        }
        static void VisitLogical(DependencyObject node, List<FrameworkElement> named, HashSet<DependencyObject> visited) {
            if (node == null || !visited.Add(node)) return;
            var element = node as FrameworkElement;
            if (element != null && !String.IsNullOrEmpty(element.Name)) named.Add(element);
            foreach (object child in LogicalTreeHelper.GetChildren(node)) {
                var dependency = child as DependencyObject;
                if (dependency != null) VisitLogical(dependency, named, visited);
            }
        }
        object InspectTestUi() {
            RequireTestHarness();
            var windows = new List<object>();
            string[] names = { "main", "widget", "settings", "custom" };
            foreach (string name in names) {
                Window window = TestWindow(name);
                window.UpdateLayout();
                var elements = new List<FrameworkElement>();
                var visited = new HashSet<DependencyObject>();
                VisitLogical(window, elements, visited);
                // Dynamically generated presets may be registered in the window name
                // scope independently of the currently attached logical panel.
                if (name == "main") for (int i = 0; i < 8; i++) {
                    var preset = window.FindName("PresetButton" + i) as FrameworkElement;
                    if (preset != null && !elements.Contains(preset)) elements.Add(preset);
                }
                var controls = new List<object>();
                foreach (FrameworkElement element in elements) {
                    double? x = null, y = null;
                    try {
                        Point point = element.TransformToAncestor(window).Transform(new Point(0, 0));
                        if (!Double.IsNaN(point.X) && !Double.IsInfinity(point.X)) x = Math.Round(point.X, 2);
                        if (!Double.IsNaN(point.Y) && !Double.IsInfinity(point.Y)) y = Math.Round(point.Y, 2);
                    } catch (InvalidOperationException) { }
                      catch (ArgumentException) { }
                    controls.Add(new {
                        name = element.Name, type = element.GetType().Name, value = InspectControlValue(element),
                        visible = element.IsVisible, enabled = element.IsEnabled, x = x, y = y,
                        width = Math.Round(element.ActualWidth, 2), height = Math.Round(element.ActualHeight, 2)
                    });
                }
                windows.Add(new {
                    name = name, visible = window.IsVisible, width = Math.Round(window.ActualWidth, 2), height = Math.Round(window.ActualHeight, 2),
                    windowState = window.WindowState.ToString(), topmost = window.Topmost, opacity = window.Opacity, controls = controls
                });
            }
            var areas = new List<object>();
            foreach (Rect area in WorkAreas()) areas.Add(new { left = area.Left, top = area.Top, width = area.Width, height = area.Height });
            return new { windows = windows, workAreas = areas, timerWakeupScheduled = ticker.IsEnabled, tickCount = testTickCount };
        }
        sealed class TestWindowState {
            public Window Window;
            public bool Visible, ShowActivated, Active;
            public WindowState State;
            public double Left, Top;
        }
        object RenderTestWindows(Dictionary<string, object> data) {
            RequireTestHarness();
            EnsureSettings();
            string suffix = "preview";
            object supplied;
            if (data != null && data.TryGetValue("suffix", out supplied)) {
                if (!(supplied is string)) throw new ApiError(400, "suffix must be a string");
                suffix = Regex.Replace((string)supplied, "[^A-Za-z0-9_-]", "_");
                if (suffix.Length > 40) suffix = suffix.Substring(0, 40);
                if (suffix.Length == 0) suffix = "preview";
            }
            var saved = new List<TestWindowState>();
            foreach (Window window in new Window[] { main, widget, settings }) {
                saved.Add(new TestWindowState { Window = window, Visible = window.IsVisible, ShowActivated = window.ShowActivated,
                    Active = window.IsActive, State = window.WindowState, Left = window.Left, Top = window.Top });
                window.ShowActivated = false;
            }
            bool wasExpanded = widgetExpanded;
            Point? savedCompactAnchor = widgetCompactAnchor;
            bool hadDraft = settingsDraftLoaded;
            var results = new List<object>();
            try {
                if (!settingsDraftLoaded) PrepareSettingsDraft();
                Update();
                results.Add(RenderTestWindow(main, "main", suffix));
                SetWidgetExpanded(false);
                results.Add(RenderTestWindow(widget, "widget-compact", suffix));
                SetWidgetExpanded(true);
                results.Add(RenderTestWindow(widget, "widget-expanded", suffix));
                results.Add(RenderTestWindow(settings, "settings", suffix));
            } finally {
                SetWidgetExpanded(wasExpanded);
                // A prepared hidden draft is not an open editing session. Keep an
                // existing draft untouched and allow a fresh one on the next opening.
                settingsDraftLoaded = hadDraft;
                foreach (TestWindowState state in saved) {
                    if (state.Visible) state.Window.Show(); else state.Window.Hide();
                    state.Window.WindowState = state.State;
                    state.Window.Left = state.Left; state.Window.Top = state.Top;
                    state.Window.ShowActivated = state.ShowActivated;
                }
                foreach (TestWindowState state in saved) if (state.Active && state.Visible && state.State != WindowState.Minimized) state.Window.Activate();
                widgetCompactAnchor = savedCompactAnchor;
            }
            return results;
        }
        object RenderTestWindow(Window window, string name, string suffix) {
            window.WindowState = WindowState.Normal;
            window.Show(); window.UpdateLayout();
            var content = window.Content as FrameworkElement;
            if (content == null || content.ActualWidth <= 0 || content.ActualHeight <= 0)
                throw new ApiError(400, "Window has no renderable content: " + name);
            int width = (int)Math.Ceiling(window.ActualWidth), height = (int)Math.Ceiling(window.ActualHeight);
            if (width < 1 || height < 1 || width > 4096 || height > 4096) throw new ApiError(400, "Invalid render dimensions");
            // A VisualBrush preserves the whole content while reflecting a widget
            // LayoutTransform in its final window dimensions, including larger sizes.
            var drawing = new DrawingVisual();
            using (DrawingContext context = drawing.RenderOpen()) {
                context.PushOpacity(window.Opacity);
                context.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, content.ActualWidth, content.ActualHeight) }, null, new Rect(0, 0, width, height));
                context.Pop();
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(folder, name + "-" + suffix + ".png");
            using (var stream = File.Create(path)) encoder.Save(stream);
            return new { name = name, file = path, width = width, height = height };
        }
    }
}
