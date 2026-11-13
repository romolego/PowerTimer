using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace PowerTimer {
 public sealed partial class TimerApplication {
  const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
  const string ResetPositionCaption = "Вернуть в угол";
  bool settingsDraftLoaded, loadingSettings, resetWidgetOnSave;
  string ReadRunValue() { using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key == null ? null : key.GetValue("PowerTimer") as string; }
  bool GetAutostart() {
   if (test) return prefs != null && prefs.startWithWindows;
   string value = ReadRunValue(), exe = "\"" + Assembly.GetExecutingAssembly().Location + "\"";
   return value != null && (value.Equals(exe, StringComparison.OrdinalIgnoreCase) || value.StartsWith(exe + " ", StringComparison.OrdinalIgnoreCase));
  }
  void WriteRunValue(string value) {
   if (test) return;
   using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) { if (value == null) key.DeleteValue("PowerTimer", false); else key.SetValue("PowerTimer", value, RegistryValueKind.String); }
  }
  object SettingsSnapshot() { Preferences copy = prefs.Clone(); copy.startWithWindows = GetAutostart(); return new { settings = copy }; }
  void CommitPreferences(Preferences next, bool resetPosition) {
   next.Validate(); Preferences before = prefs;
   string oldRun = test ? null : ReadRunValue();
   string newRun = next.startWithWindows ? "\"" + Assembly.GetExecutingAssembly().Location + "\" --startup" : null;
   bool registryChanged = !test && oldRun != newRun;
   try {
    if (registryChanged) WriteRunValue(newRun);
    Store.WriteChanged(Path.Combine(folder, "settings.json"), next, ref savedSettings);
   } catch {
    if (registryChanged) { try { WriteRunValue(oldRun); } catch (Exception ex) { Log("Autostart rollback: " + ex.Message); } }
    throw;
   }
   prefs = next; previewTheme = null;
   if (before.defaultAction != next.defaultAction) selectedPowerAction = next.defaultAction;
   if (before.scheduleTimeOnly != next.scheduleTimeOnly || before.scheduleNextHour != next.scheduleNextHour)
    InitializeScheduleInputs();
   ApplyPreferences(before.presetMinutes[0] != next.presetMinutes[0]);
   if (resetPosition || (before.rememberWidgetPosition && !next.rememberWidgetPosition)) PlaceWidget(false);
   SaveWidgetPosition(); Persist();
  }
  static string SelectedTag(ComboBox box) { var item = box.SelectedItem as ComboBoxItem; return item == null ? null : Convert.ToString(item.Tag); }
  void SelectTag(string name, string tag) {
   var box = Find<ComboBox>(settings, name);
   foreach (ComboBoxItem item in box.Items) if (Convert.ToString(item.Tag) == tag) { box.SelectedItem = item; return; }
  }
  void SetCheck(string name, bool value) { Find<CheckBox>(settings, name).IsChecked = value; }
  bool Check(string name) { return Find<CheckBox>(settings, name).IsChecked == true; }
  void WireSettings() {
   DragOn(Find<Grid>(settings, "SettingsDrag"), settings);
   settings.Closing += delegate(object s, System.ComponentModel.CancelEventArgs e) { if (exiting) return; e.Cancel = true; CancelSettings(); };
   settings.PreviewKeyDown += delegate(object s, KeyEventArgs e) {
    if (e.Key != Key.Escape || (Keyboard.FocusedElement is ComboBox && ((ComboBox)Keyboard.FocusedElement).IsDropDownOpen)) return;
    CancelSettings(); e.Handled = true;
   };
   Find<Button>(settings, "SettingsClose").Click += delegate { CancelSettings(); };
   Find<Button>(settings, "CancelSettingsButton").Click += delegate { CancelSettings(); };
   var explanation = Find<TextBlock>(settings, "DefaultPresetExplanation");
   Find<Button>(settings, "DefaultPresetHelp").Click += delegate {
    explanation.Visibility = explanation.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
   };
   Find<Button>(settings, "SaveSettingsButton").Click += delegate {
    try {
     Preferences next = ReadSettingsDraft(); CommitPreferences(next, resetWidgetOnSave);
     settings.Hide(); settingsDraftLoaded = false; resetWidgetOnSave = false;
    } catch (Exception ex) {
     var error = Find<TextBlock>(settings, "SettingsError"); error.Text = ex.Message; error.Visibility = Visibility.Visible;
    }
   };
   Find<ComboBox>(settings, "ThemeSelect").SelectionChanged += delegate {
    if (loadingSettings || !settingsDraftLoaded) return;
    previewTheme = SelectedTag(Find<ComboBox>(settings, "ThemeSelect")); ApplyTheme(previewTheme);
   };
   var autostart = Find<CheckBox>(settings, "AutostartCheck");
   autostart.Checked += delegate { Find<CheckBox>(settings, "StartupTrayCheck").IsEnabled = true; };
   autostart.Unchecked += delegate { Find<CheckBox>(settings, "StartupTrayCheck").IsEnabled = false; };
   Find<Slider>(settings, "WidgetOpacitySlider").ValueChanged += delegate {
    Find<TextBlock>(settings, "WidgetOpacityText").Text = ((int)Find<Slider>(settings, "WidgetOpacitySlider").Value) + "%";
   };
   Find<Button>(settings, "ResetWidgetPosition").Click += delegate {
    resetWidgetOnSave = true; Find<Button>(settings, "ResetWidgetPosition").Content = ResetPositionCaption + " ✓";
   };
  }
  void PrepareSettingsDraft() {
   loadingSettings = true;
   try {
    SelectTag("ThemeSelect", prefs.theme);
    SelectTag("DefaultActionSelect", prefs.defaultAction);
    SelectTag("ScheduleDateOption", prefs.scheduleTimeOnly ? "time" : "date");
    SelectTag("ScheduleTimeOption", prefs.scheduleNextHour ? "hour" : "later");
    for (int i = 0; i < 7; i++) Find<TextBox>(settings, "Preset" + i).Text = prefs.presetMinutes[i].ToString();
    SetCheck("KeepAwakeCheck", prefs.keepAwake); SetCheck("AutostartCheck", GetAutostart());
    SetCheck("StartupTrayCheck", prefs.startInTray); SetCheck("CloseToTrayCheck", prefs.closeToTray);
    SetCheck("MinimizeToTrayCheck", prefs.minimizeToTray); SetCheck("NotifyCheck", prefs.notifyBeforeShutdown);
    SetCheck("WidgetTopmostCheck", prefs.widgetTopmost); SetCheck("WidgetRememberPositionCheck", prefs.rememberWidgetPosition);
    Find<CheckBox>(settings, "StartupTrayCheck").IsEnabled = GetAutostart();
    SelectTag("WidgetSizeSelect", prefs.widgetSize); Find<Slider>(settings, "WidgetOpacitySlider").Value = prefs.widgetOpacity;
    Find<TextBlock>(settings, "WidgetOpacityText").Text = prefs.widgetOpacity + "%";
    Find<TextBlock>(settings, "SettingsError").Text = ""; Find<TextBlock>(settings, "SettingsError").Visibility = Visibility.Collapsed;
    Find<TextBlock>(settings, "DefaultPresetExplanation").Visibility = Visibility.Collapsed;
    Find<Button>(settings, "ResetWidgetPosition").Content = ResetPositionCaption;
    resetWidgetOnSave = false; settingsDraftLoaded = true;
   } finally { loadingSettings = false; }
  }
  Preferences ReadSettingsDraft() {
   Preferences next = prefs.Clone();
   next.defaultAction = SelectedTag(Find<ComboBox>(settings, "DefaultActionSelect"));
   next.scheduleTimeOnly = SelectedTag(Find<ComboBox>(settings, "ScheduleDateOption")) == "time";
   next.scheduleNextHour = SelectedTag(Find<ComboBox>(settings, "ScheduleTimeOption")) == "hour";
   next.theme = SelectedTag(Find<ComboBox>(settings, "ThemeSelect"));
   for (int i = 0; i < 7; i++) next.presetMinutes[i] = InputInt(settings, "Preset" + i);
   next.keepAwake = Check("KeepAwakeCheck"); next.startWithWindows = Check("AutostartCheck"); next.startInTray = Check("StartupTrayCheck");
   next.closeToTray = Check("CloseToTrayCheck"); next.minimizeToTray = Check("MinimizeToTrayCheck"); next.notifyBeforeShutdown = Check("NotifyCheck");
   next.widgetTopmost = Check("WidgetTopmostCheck"); next.rememberWidgetPosition = Check("WidgetRememberPositionCheck");
   next.widgetSize = SelectedTag(Find<ComboBox>(settings, "WidgetSizeSelect")); next.widgetOpacity = (int)Find<Slider>(settings, "WidgetOpacitySlider").Value;
   next.Validate(); return next;
  }
  void EnsureSettings() {
   if (settings != null) return;
   settings = LoadWindow("Settings.xaml"); settings.Icon = main.Icon;
   settings.MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 24);
   if (test) { settings.Title += " [ТЕСТ]"; Find<TextBlock>(settings, "SettingsTestBadge").Visibility = Visibility.Visible; }
   WireSettings();
  }
  void OpenSettings() {
   EnsureSettings();
   if (settings.IsVisible) { settings.Activate(); return; }
   PrepareSettingsDraft(); previewTheme = prefs.theme;
   settings.Topmost = widget.IsVisible && widget.Topmost;
   settings.WindowStartupLocation = WindowStartupLocation.CenterScreen;
   settings.Show(); settings.WindowState = WindowState.Normal; settings.Activate();
  }
  void CancelSettings() { settings.Hide(); settingsDraftLoaded = false; resetWidgetOnSave = false; previewTheme = null; ApplyTheme(prefs.theme); }
 }
}
