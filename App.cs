using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
namespace PowerTimer {
 public sealed partial class TimerApplication : IDisposable {
  [DllImport("kernel32.dll", SetLastError = true)] static extern uint SetThreadExecutionState(uint flags);
  [DllImport("powrprof.dll")]
  [return: MarshalAs(UnmanagedType.U1)]
  static extern bool IsPwrHibernateAllowed();
  const uint Continuous = 0x80000000, SystemRequired = 1;
  readonly Application app; readonly string folder; readonly bool test;
  Preferences prefs; readonly TimerEngine engine; readonly SystemClock clock;
  readonly Window main, widget;
  readonly ComboBox actionPicker, timingModePicker, scheduleClock;
  readonly DatePicker scheduleDate;
  readonly System.ComponentModel.DependencyPropertyDescriptor scheduleTextProperty;
  DateTimeOffset? scheduleTarget;
  string scheduleError;
  bool currentTimeDraft;
  bool loadingSchedule, selectingTimingMode, scheduleMode;
  Window settings, custom;
  int selectedSeconds, selectedIndex;
  string selectedPowerAction;
  readonly Forms.NotifyIcon tray;
  readonly Forms.ToolStripMenuItem trayWidget, trayPause, trayCancel, trayShutdown, trayHibernate;
  readonly System.Drawing.Icon trayIcon;
  readonly DispatcherTimer ticker; LocalApi api;
  bool exiting, exitPending, initialized, sleepHeld, warned, widgetExpanded;
  double lastTick, noticeUntil;
  string savedSettings, savedTimer;
  string lastRenderKey, uiNotice, effectiveTheme, previewTheme;
  Point? widgetCompactAnchor;

  public TimerApplication(Application app, string folder, bool test) {
   this.app = app; this.folder = folder; this.test = test;
   prefs = Store.Read<Preferences>(Path.Combine(folder, "settings.json")) ?? new Preferences(); prefs.Normalize();
   selectedPowerAction = prefs.defaultAction;
   if (!test) prefs.startWithWindows = GetAutostart();
   clock = new SystemClock(); engine = new TimerEngine(clock, ExecutePowerAction);
   engine.Restore(Store.Read<TimerRecord>(Path.Combine(folder, "timer.json")));
   effectiveTheme = Theme.Apply(app, prefs.theme);
   using (Stream r = Resource("Styles.xaml")) app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(r));
   main = LoadWindow("Main.xaml"); widget = LoadWindow("Widget.xaml");
   actionPicker = Find<ComboBox>(main, "PowerActionSelect");
   timingModePicker = Find<ComboBox>(main, "TimingModeSelect");
   scheduleDate = Find<DatePicker>(main, "ScheduleDate");
   scheduleClock = Find<ComboBox>(main, "ScheduleClock");
   scheduleTextProperty = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ComboBox.TextProperty, typeof(ComboBox));
   scheduleMode = prefs.defaultTimingMode == "schedule";
   main.MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 24);
   using (Stream r = Resource("icon.ico")) {
    trayIcon = new System.Drawing.Icon(r);
    main.Icon = Imaging.CreateBitmapSourceFromHIcon(trayIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
    tray = new Forms.NotifyIcon { Icon = trayIcon, Visible = true, Text = "Power Timer" };
   }
   var menu = new Forms.ContextMenuStrip();
   menu.Items.Add("Открыть главное окно", null, delegate { OnUi(ShowMain); });
   trayWidget = new Forms.ToolStripMenuItem("Показать виджет", null, delegate { OnUi(ShowWidget); }); menu.Items.Add(trayWidget);
   menu.Items.Add("Настройки", null, delegate { OnUi(OpenSettings); });
   trayShutdown = new Forms.ToolStripMenuItem("Выключить через"); menu.Items.Add(trayShutdown);
   trayHibernate = new Forms.ToolStripMenuItem("Гибернация через"); menu.Items.Add(trayHibernate);
   trayPause = new Forms.ToolStripMenuItem("Пауза", null, delegate { OnUi(delegate { Try(TogglePause); }); }); menu.Items.Add(trayPause);
   trayCancel = new Forms.ToolStripMenuItem("Отменить таймер", null, delegate { OnUi(engine.Cancel); }); menu.Items.Add(trayCancel);
   menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("Выход", null, delegate { OnUi(RequestExit); });
   tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { OnUi(ShowMain); };
   WireUi();
   if (scheduleMode) InitializeScheduleInputs();
   ApplyPreferences(true);
   ticker = new DispatcherTimer(DispatcherPriority.Normal, app.Dispatcher) { Interval = TimeSpan.FromSeconds(1) }; ticker.Tick += delegate { Tick(); };
   engine.Changed += delegate {
    if (Visible(custom)) CloseCustom();
    warned = false; uiNotice = null; lastTick = clock.Elapsed;
    Persist(true); UpdateSleep(); Update(); ScheduleTick();
   };
   SystemEvents.UserPreferenceChanged += OnSystemPreferences; SystemEvents.DisplaySettingsChanged += OnDisplaysChanged; SystemEvents.TimeChanged += OnTimeChanged;
  }
  Stream Resource(string name) { return Assembly.GetExecutingAssembly().GetManifestResourceStream("PowerTimer." + name); }
  Window LoadWindow(string name) { using (Stream r = Resource(name)) return (Window)XamlReader.Load(r); }
  T Find<T>(Window window, string name) where T : class { T item = window.FindName(name) as T; if (item == null) throw new InvalidOperationException("Отсутствует элемент: " + name); return item; }
  void Click(Window w, string name, Action action) { Find<Button>(w, name).Click += delegate { Try(action); }; }
  static bool IsControl(DependencyObject item) {
   for (DependencyObject n = item; n != null; n = n is Visual ? VisualTreeHelper.GetParent(n) : LogicalTreeHelper.GetParent(n))
    if (n is ButtonBase || n is TextBoxBase || n is Selector || n is Slider) return true;
   return false;
  }
  void DragOn(Grid handle, Window window) {
   handle.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) {
    if (e.LeftButton != MouseButtonState.Pressed || IsControl(e.OriginalSource as DependencyObject)) return;
    window.DragMove();
   };
  }
  void WireWidgetDrag(Grid handle) {
   Point press = new Point();
   handle.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) {
    if (IsControl(e.OriginalSource as DependencyObject)) return;
    e.Handled = true;
    if (!widgetExpanded && e.ClickCount == 2) {
     Try(ShowMain);
     return;
    }
    press = e.GetPosition(widget);
    handle.CaptureMouse();
   };
   handle.MouseLeftButtonUp += delegate { if (handle.IsMouseCaptured) handle.ReleaseMouseCapture(); };
   handle.MouseMove += delegate(object s, MouseEventArgs e) {
    if (!handle.IsMouseCaptured) return;
    if (e.LeftButton != MouseButtonState.Pressed) { handle.ReleaseMouseCapture(); return; }
    Vector motion = e.GetPosition(widget) - press;
    if (Math.Abs(motion.X) < SystemParameters.MinimumHorizontalDragDistance &&
        Math.Abs(motion.Y) < SystemParameters.MinimumVerticalDragDistance) return;
    handle.ReleaseMouseCapture();
    widget.DragMove();
    ClampWidget();
    if (widgetExpanded) widgetCompactAnchor = new Point(widget.Left, widget.Top);
    SaveWidgetPosition();
    Persist();
   };
  }
  void WireUi() {
   if (test) {
    widget.Title += " [ТЕСТ]";
    Find<TextBlock>(main, "TestBadge").Visibility = Visibility.Visible;
   }
   foreach (Window window in new[] { main, widget }) {
    window.Closing += delegate(object s, System.ComponentModel.CancelEventArgs e) { if (exiting) return; e.Cancel = true; CloseRequested(); };
    window.StateChanged += delegate { if (window.WindowState == WindowState.Minimized && prefs.minimizeToTray) { HideAll(); window.WindowState = WindowState.Normal; } };
    window.PreviewKeyDown += delegate(object s, KeyEventArgs e) {
     if (e.Key != Key.Escape ||
         (window == main && (actionPicker.IsDropDownOpen || timingModePicker.IsDropDownOpen || scheduleDate.IsDropDownOpen))) return;
     HideAll(); e.Handled = true;
    };
   }
   DragOn(Find<Grid>(main, "DragArea"), main);
   WireWidgetDrag(Find<Grid>(widget, "WidgetDrag"));
   Click(main, "WidgetButton", ShowWidget); Click(main, "SettingsButton", OpenSettings);
   Click(main, "HideButton", delegate { if (prefs.minimizeToTray) HideAll(); else main.WindowState = WindowState.Minimized; });
   Click(main, "CloseButton", CloseRequested);
   actionPicker.SelectionChanged += delegate {
    string action = SelectedTag(actionPicker);
    if (engine.Active || action == null || action == selectedPowerAction) return;
    selectedPowerAction = action;
    Update();
   };
   timingModePicker.SelectionChanged += delegate {
    if (engine.Active || selectingTimingMode) return;
    scheduleMode = SelectedTag(timingModePicker) == "schedule";
    if (scheduleMode) InitializeScheduleInputs();
    uiNotice = null;
    Update();
   };
   scheduleDate.SelectedDateChanged += delegate { ScheduleDateChanged(); };
   scheduleDate.CalendarOpened += delegate {
    var now = new DateTimeOffset(clock.UtcNow);
    if (UpdateScheduleDates(now)) ScheduleDateChanged();
    else UpdateScheduleValidation(now, false);
   };
   scheduleTextProperty.AddValueChanged(scheduleClock, ScheduleClockChanged);
   scheduleClock.SelectionChanged += delegate {
    if (!loadingSchedule && currentTimeDraft && scheduleClock.SelectedItem != null)
     ScheduleClockChanged(null, EventArgs.Empty);
   };
   scheduleClock.DropDownOpened += delegate {
    var now = new DateTimeOffset(clock.UtcNow);
    if (!prefs.scheduleTimeOnly && UpdateScheduleDates(now)) ScheduleDateChanged();
    else {
     RefreshScheduleOptions(now, false);
     UpdateScheduleValidation(now, false);
    }
   };
   main.Activated += delegate {
    if (!engine.Active && scheduleMode) UpdateScheduleValidation(new DateTimeOffset(clock.UtcNow), false);
   };
   Click(main, "StartButton", StartFromMain);
   Click(main, "PauseButton", TogglePause); Click(main, "CancelButton", engine.Cancel);
   Click(widget, "WidgetPause", delegate { if (engine.Active) TogglePause(); else StartTimer(selectedSeconds, CurrentPowerAction); });
   Click(widget, "WidgetExpandButton", delegate { SetWidgetExpanded(!widgetExpanded); });
   Click(widget, "WidgetCancel", engine.Cancel); Click(widget, "WidgetOpenMain", ShowMain); Click(widget, "WidgetHide", HideAll);
   widget.SizeChanged += delegate { if (initialized && widget.IsVisible) ClampWidget(); };
  }
  void ScheduleClockChanged(object sender, EventArgs e) {
   if (loadingSchedule) return;
   currentTimeDraft = false;
   ScheduleInputChanged(new DateTimeOffset(clock.UtcNow));
  }
  void ScheduleDateChanged() {
   if (loadingSchedule || prefs.scheduleTimeOnly) return;
   var now = new DateTimeOffset(clock.UtcNow);
   currentTimeDraft = false;
   RefreshScheduleOptions(now, true);
   ScheduleInputChanged(now);
  }
  void ScheduleInputChanged(DateTimeOffset now) {
   UpdateScheduleValidation(now, true);
   if (!engine.Active && uiNotice != null) {
    uiNotice = null;
    Update();
    ScheduleTick();
   }
  }
  void InitializeScheduleInputs() {
   var now = new DateTimeOffset(clock.UtcNow);
   DateTime initial = ScheduleTime.InitialLocal(now, prefs.scheduleInitialTime, TimeZoneInfo.Local);
   currentTimeDraft = prefs.scheduleInitialTime == "now";
   loadingSchedule = true;
   try {
    scheduleDate.SelectedDate = null;
    UpdateScheduleDates(now);
    DateTime date = initial.Date < scheduleDate.DisplayDateStart.Value ? scheduleDate.DisplayDateStart.Value : initial.Date;
    scheduleDate.SelectedDate = date;
    scheduleDate.DisplayDate = date;
    var dateField = Find<StackPanel>(main, "ScheduleDateField");
    dateField.Visibility = prefs.scheduleTimeOnly ? Visibility.Collapsed : Visibility.Visible;
    var timeField = Find<StackPanel>(main, "ScheduleTimeField");
    Grid.SetColumn(timeField, prefs.scheduleTimeOnly ? 0 : 1);
    Grid.SetColumnSpan(timeField, prefs.scheduleTimeOnly ? 2 : 1);
    scheduleClock.SelectedItem = null;
    scheduleClock.Text = initial.ToString("HH:mm", CultureInfo.InvariantCulture);
   } finally { loadingSchedule = false; }
   bool advance = !prefs.scheduleTimeOnly && initial.Date != scheduleDate.SelectedDate.Value;
   if (advance) currentTimeDraft = false;
   RefreshScheduleOptions(now, advance);
   UpdateScheduleValidation(now, true);
  }
  bool UpdateScheduleDates(DateTimeOffset now) {
   DateTime first = ScheduleTime.BoundaryDate(now, TimeZoneInfo.Local, false);
   DateTime last = ScheduleTime.BoundaryDate(now, TimeZoneInfo.Local, true);
   DateTime? selected = scheduleDate.SelectedDate;
   bool wasLoading = loadingSchedule;
   loadingSchedule = true;
   try {
    if (selected.HasValue && (selected.Value < first || selected.Value > last)) scheduleDate.SelectedDate = null;
    // WPF coerces each bound against the other one.
    if (scheduleDate.DisplayDateStart > last) scheduleDate.DisplayDateStart = first;
    scheduleDate.DisplayDateEnd = last;
    scheduleDate.DisplayDateStart = first;
    if (!scheduleDate.SelectedDate.HasValue && selected.HasValue)
     scheduleDate.SelectedDate = selected.Value < first ? first : last;
   } finally { loadingSchedule = wasLoading; }
   return selected != scheduleDate.SelectedDate;
  }
  DateTimeOffset ResolveScheduleInput(DateTimeOffset now) {
   DateTime? date = prefs.scheduleTimeOnly ? null : scheduleDate.SelectedDate;
   if (!prefs.scheduleTimeOnly && !date.HasValue) throw new ArgumentException("Выберите дату.");
   return ScheduleTime.Resolve(scheduleClock.Text, date, now, TimeZoneInfo.Local);
  }
  void RefreshScheduleOptions(DateTimeOffset now, bool adjustTime) {
   DateTime? date = prefs.scheduleTimeOnly ? null : scheduleDate.SelectedDate;
   var options = ScheduleTime.Options(date, now, TimeZoneInfo.Local, 15);
   if (date.HasValue && options.Count == 0) options = ScheduleTime.Options(date, now, TimeZoneInfo.Local, 1);
   string text = scheduleClock.Text;
   if (adjustTime && options.Count > 0) {
    bool valid;
    try { valid = ScheduleTime.InWindow(ResolveScheduleInput(now), now); }
    catch (ArgumentException) { valid = false; }
    if (!valid) text = options[0];
   }
   var previous = scheduleClock.ItemsSource as List<string>;
   if (previous != null && previous.SequenceEqual(options) && scheduleClock.Text == text) return;
   loadingSchedule = true;
   try {
    scheduleClock.ItemsSource = options;
    scheduleClock.SelectedItem = options.Contains(text) && !currentTimeDraft ? text : null;
    scheduleClock.Text = text;
   } finally { loadingSchedule = false; }
  }
  void UpdateScheduleValidation(DateTimeOffset now, bool changed) {
   if (changed) scheduleTarget = null;
   scheduleError = null;
   try {
    if (currentTimeDraft) throw new ArgumentException("Выберите время в будущем.");
    if (!scheduleTarget.HasValue) scheduleTarget = ResolveScheduleInput(now);
    if (scheduleTarget.Value <= now) throw new ArgumentException("Выберите время в будущем.");
    if (!ScheduleTime.InWindow(scheduleTarget.Value, now)) throw new ArgumentException("Выберите время в пределах ближайших 7 суток.");
   } catch (ArgumentException ex) {
    scheduleError = ex.Message;
   }
   bool tomorrow = scheduleError == null && prefs.scheduleTimeOnly && scheduleTarget.Value.LocalDateTime.Date > now.LocalDateTime.Date;
   Find<TextBlock>(main, "ScheduleTimeLabel").Text = tomorrow ? "Время · завтра" : "Время";
   UpdateStartButton();
  }
  void UpdateStartButton() {
   var start = Find<Button>(main, "StartButton");
   start.IsEnabled = !scheduleMode || scheduleError == null;
   start.ToolTip = scheduleMode ? scheduleError : null;
  }
  void StartFromMain() {
   if (!scheduleMode) {
    StartTimer(selectedSeconds, CurrentPowerAction);
    return;
   }
   UpdateScheduleValidation(new DateTimeOffset(clock.UtcNow), false);
   if (scheduleError != null) throw new ArgumentException(scheduleError);
   StartSchedule(scheduleTarget.Value, CurrentPowerAction);
  }
  static bool Visible(Window window) { return window != null && window.IsVisible; }
  void EnsureCustom() {
   if (custom != null) return;
   custom = LoadWindow("Custom.xaml");
   custom.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!exiting) { e.Cancel = true; CloseCustom(); } };
   Click(custom, "CustomCancel", CloseCustom);
   Click(custom, "CustomAccept", delegate {
    string unit = Convert.ToString(((ComboBoxItem)Find<ComboBox>(custom, "CustomUnit").SelectedItem).Tag);
    try { selectedSeconds = DurationInput.Parse(Find<TextBox>(custom, "CustomValue").Text, unit == "hours"); }
    catch (ArgumentException ex) {
     Find<TextBlock>(custom, "CustomError").Text = ex.Message; Find<TextBlock>(custom, "CustomError").Visibility = Visibility.Visible; return;
    }
    selectedIndex = 7; CloseCustom(); RefreshSelection();
   });
   custom.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { CloseCustom(); e.Handled = true; } };
  }
  static int InputInt(Window w, string name) { int n; if (!Int32.TryParse(((TextBox)w.FindName(name)).Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out n)) throw new ArgumentException("Введите целое число."); return n; }
  static string ShortDuration(int seconds) { return DurationInput.Short(seconds); }
  void BuildPresets() {
   var panel = Find<UniformGrid>(main, "Presets");
   for (int i = 0; i < 8; i++) if (main.FindName("PresetButton" + i) != null) main.UnregisterName("PresetButton" + i);
   panel.Children.Clear();
   foreach (var menu in new[] { trayShutdown, trayHibernate })
    while (menu.DropDownItems.Count > 0) menu.DropDownItems[0].Dispose();
   for (int i = 0; i < prefs.presetMinutes.Length; i++) {
    int seconds = prefs.presetMinutes[i] * 60;
    var b = new Button { Name = "PresetButton" + i, Content = ShortDuration(seconds), Margin = new Thickness(3), Tag = seconds };
    int index = i; AutomationProperties.SetName(b, "Выбрать " + ShortDuration(seconds)); b.Click += delegate { Try(delegate { selectedSeconds = seconds; selectedIndex = index; RefreshSelection(); }); };
    main.RegisterName(b.Name, b); panel.Children.Add(b);
    AddTrayPreset(trayShutdown, seconds, TimerEngine.ShutdownAction);
    AddTrayPreset(trayHibernate, seconds, TimerEngine.HibernateAction);
   }
   var own = new Button { Name = "PresetButton7", Content = "Своё время", Margin = new Thickness(3) };
   own.Click += delegate { Try(OpenCustom); }; main.RegisterName(own.Name, own); panel.Children.Add(own);
  }
  void AddTrayPreset(Forms.ToolStripMenuItem menu, int seconds, string action) {
   menu.DropDownItems.Add(ShortDuration(seconds), null, delegate {
    OnUi(delegate { if (!engine.Active) Try(delegate { StartTimer(seconds, action); }); });
   });
  }
  void OpenCustom() {
   EnsureCustom();
   Find<TextBox>(custom, "CustomValue").Text = (selectedSeconds / 60m).ToString("0.########", CultureInfo.CurrentCulture);
   Find<ComboBox>(custom, "CustomUnit").SelectedIndex = 0; Find<TextBlock>(custom, "CustomError").Visibility = Visibility.Collapsed;
   custom.Owner = main; custom.Show(); main.IsEnabled = false; Find<TextBox>(custom, "CustomValue").Focus(); Find<TextBox>(custom, "CustomValue").SelectAll();
  }
  void CloseCustom() { if (custom != null) custom.Hide(); main.IsEnabled = true; }
  void RefreshSelection() {
   var panel = Find<UniformGrid>(main, "Presets");
   for (int i = 0; i < panel.Children.Count; i++) {
    var b = (Button)panel.Children[i]; bool chosen = i == selectedIndex;
    b.SetResourceReference(Button.BackgroundProperty, chosen ? "AccentBrush" : "RaisedBg");
    b.SetResourceReference(Button.ForegroundProperty, chosen ? "AccentTextBrush" : "TextBrush");
    b.SetResourceReference(Button.BorderBrushProperty, chosen ? "AccentBrush" : "BorderBrush");
    if (i == 7) b.Content = chosen ? ShortDuration(selectedSeconds) : "Своё время";
   }
  }
  void ApplyPreferences(bool resetInputs) {
   if (resetInputs) selectedSeconds = prefs.presetMinutes[0] * 60;
   selectedIndex = selectedSeconds % 60 == 0 ? Array.IndexOf(prefs.presetMinutes, selectedSeconds / 60) : -1; if (selectedIndex < 0) selectedIndex = 7;
   BuildPresets(); RefreshSelection(); ApplyTheme(prefs.theme); widget.Topmost = prefs.widgetTopmost; widget.Opacity = prefs.widgetOpacity / 100.0;
   double scale = prefs.widgetSize == "medium" ? 1.2 : prefs.widgetSize == "large" ? 1.4 : 1;
   Find<Border>(widget, "WidgetRoot").LayoutTransform = new ScaleTransform(scale, scale);
   if (!initialized) PlaceWidget(true); else { widget.UpdateLayout(); ClampWidget(); }
   UpdateSleep(); lastRenderKey = null; Update();
  }
  void ApplyTheme(string choice) { effectiveTheme = Theme.Apply(app, choice); lastRenderKey = null; if (main != null) Update(); }
  void OnSystemPreferences(object sender, UserPreferenceChangedEventArgs e) { OnUi(delegate { string choice = previewTheme ?? prefs.theme; if (choice == "system") ApplyTheme(choice); }); }
  void OnTimeChanged(object sender, EventArgs e) {
   OnUi(delegate {
    TimeZoneInfo.ClearCachedData();
    if (scheduleMode) {
     var now = new DateTimeOffset(clock.UtcNow);
     if (!prefs.scheduleTimeOnly && UpdateScheduleDates(now)) ScheduleDateChanged();
     else RefreshScheduleOptions(now, false);
     UpdateScheduleValidation(now, true);
    }
    Persist(true);
    lastRenderKey = null;
    Update();
    ScheduleTick();
   });
  }
  void OnDisplaysChanged(object sender, EventArgs e) { OnUi(ClampWidget); }
  public void Start(bool hidden, bool compact, bool startup) {
   byte[] bytes = new byte[32]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes); string token = Convert.ToBase64String(bytes);
   api = new LocalApi(token, DispatchRequest, test ? 47822 : 47821);
   Store.Write(Path.Combine(folder, "api.json"), new { version = 1, baseUrl = "http://127.0.0.1:" + api.Port + "/", token = token, pid = Process.GetCurrentProcess().Id, testMode = test, executable = Assembly.GetExecutingAssembly().Location });
   initialized = true; Persist(true); UpdateSleep(); Update(); ScheduleTick();
   if (hidden || (startup && prefs.startInTray)) HideAll(); else if (compact || prefs.widgetMode) ShowWidget(); else ShowMain();
   Log(test ? "Started TEST 2.6.1 (no power actions)" : "Started 2.6.1");
  }
  void OnUi(Action action) {
   if (!exiting && !exitPending && !app.Dispatcher.HasShutdownStarted)
    app.Dispatcher.BeginInvoke(new Action(delegate { if (!exiting && !exitPending) action(); }));
  }
  void Try(Action action) { if (exiting || exitPending) return; try { uiNotice = null; action(); Update(); ScheduleTick(); } catch (Exception ex) { ShowError(ex.Message); } }
  public void ShowError(string text) { uiNotice = text; noticeUntil = clock.Elapsed + 12; lastRenderKey = null; Update(); ScheduleTick(); if (!main.IsVisible && !Visible(settings)) ShowMain(); }
  string CurrentPowerAction { get { return engine.Active ? engine.ScheduledAction : selectedPowerAction; } }
  void EnsureActionAvailable(string action) {
   if (action == TimerEngine.HibernateAction && !test && !IsPwrHibernateAllowed())
    throw new InvalidOperationException("Гибернация недоступна в текущих настройках Windows.");
  }
  void StartTimer(double seconds, string action) {
   EnsureActionAvailable(action);
   engine.Start(seconds, action);
  }
  void StartSchedule(DateTimeOffset target, string action) {
   EnsureActionAvailable(action);
   engine.StartAt(target, action);
  }
  void ResumeTimer() {
   if (engine.State == "paused") EnsureActionAvailable(engine.ScheduledAction);
   engine.Resume();
  }
  void TogglePause() { if (engine.State == "paused") ResumeTimer(); else engine.Pause(); }
  void Tick() {
   if (test) testTickCount++;
   double elapsed = clock.Elapsed;
   if (elapsed - lastTick > 15 && lastTick > 0 && engine.State == "running" && engine.Remaining <= 0) {
    engine.StopWithMessage("Срок прошёл во время сна или задержки. Таймер снят."); uiNotice = engine.Message; noticeUntil = elapsed + 15;
    if (prefs.notifyBeforeShutdown) Notify("Таймер снят", "Срок прошёл во время сна или приостановки приложения.");
   }
   lastTick = elapsed; engine.Tick(); if (uiNotice != null && elapsed > noticeUntil) uiNotice = null; Update();
   if (prefs.notifyBeforeShutdown && engine.State == "running" && engine.Remaining <= 60 && !warned) {
    warned = true;
    string title = engine.ScheduledAction == TimerEngine.HibernateAction ? "Гибернация компьютера" : "Выключение компьютера";
    Notify(title, "Осталось " + (int)Math.Ceiling(engine.Remaining) + " сек.");
   }
   ScheduleTick();
  }
  // No timer, polling, or disk heartbeat is needed when idle or paused.
  // Check a running timer at least once per second and again at its deadline.
  void ScheduleTick() {
   if (ticker == null) return;
   ticker.Stop();
   if (!initialized || exiting || exitPending) return;
   double delay = engine.State == "running" ? Math.Min(1, Math.Max(.001, engine.Remaining)) : Double.PositiveInfinity;
   if (uiNotice != null) delay = Math.Min(delay, Math.Max(.001, noticeUntil - clock.Elapsed));
   if (Double.IsPositiveInfinity(delay)) return;
   ticker.Interval = TimeSpan.FromSeconds(delay); ticker.Start();
  }
  void Update() {
   if (main == null || widget == null) return;
   string state = engine.State;
   bool active = engine.Active;
   bool scheduled = active && engine.ScheduledUtc.HasValue;
   bool showSchedule = active ? scheduled : scheduleMode;
   string action = CurrentPowerAction;
   string key = state + ":" + Math.Ceiling(engine.Remaining).ToString(CultureInfo.InvariantCulture) + ":" + uiNotice + ":" + effectiveTheme + ":" + action + ":" + showSchedule;
   if (key == lastRenderKey) return; lastRenderKey = key;
   if (active) actionPicker.IsDropDownOpen = false;
   if (SelectedTag(actionPicker) != action) actionPicker.SelectedValue = action;
   actionPicker.IsEnabled = !active;
   string mode = showSchedule ? "schedule" : "timer";
   if (active) timingModePicker.IsDropDownOpen = false;
   if (SelectedTag(timingModePicker) != mode) {
    selectingTimingMode = true;
    try { timingModePicker.SelectedValue = mode; }
    finally { selectingTimingMode = false; }
   }
   timingModePicker.IsEnabled = !active;
   bool hibernate = action == TimerEngine.HibernateAction;
   main.Title = (hibernate ? "Таймер гибернации" : "Таймер выключения") + (test ? " [ТЕСТ]" : "");
   string time = active ? TimerEngine.Format(engine.Remaining) : "--:--:--";
   string label;
   switch (state) {
    case "running": label = hibernate ? "ДО ГИБЕРНАЦИИ" : "ДО ВЫКЛЮЧЕНИЯ"; break;
    case "paused": label = "ПАУЗА"; break;
    case "completed": label = test ? "ТЕСТ ЗАВЕРШЁН" : engine.Message.ToUpperInvariant(); break;
    case "error": label = "ОШИБКА"; break;
    default: label = "ТАЙМЕР НЕ ЗАПУЩЕН"; break;
   }
   DateTime? due = engine.Deadline;
   string deadline = due.HasValue ? due.Value.ToLocalTime().ToString("dd.MM · HH:mm:ss") : "";
   Brush color = (Brush)app.Resources[state == "running" && engine.Remaining <= 60 ? "UrgentBrush" : "AccentBrush"];
   Find<TextBlock>(main, "Countdown").Text = time; Find<TextBlock>(main, "Countdown").Foreground = color;
   Find<TextBlock>(widget, "WidgetCountdown").Text = time; Find<TextBlock>(widget, "WidgetCountdown").Foreground = color;
   var widgetDeadline = Find<TextBlock>(widget, "WidgetDeadline");
   widgetDeadline.Text = scheduled ? "К " + engine.ScheduledUtc.Value.ToLocalTime().ToString("dd.MM · HH:mm") : "";
   widgetDeadline.Visibility = scheduled ? Visibility.Visible : Visibility.Collapsed;

   Find<TextBlock>(main, "StateText").Text = label; Find<TextBlock>(main, "DeadlineText").Text = deadline;

   var notice = Find<TextBlock>(main, "NoticeText"); notice.Text = uiNotice ?? (state == "error" ? engine.Message : ""); notice.Visibility = String.IsNullOrEmpty(notice.Text) ? Visibility.Collapsed : Visibility.Visible;
   Find<ProgressBar>(main, "Progress").Value = active && engine.Total > 0 ? Math.Min(100, engine.Remaining / engine.Total * 100) : 0;
   Find<Button>(main, "PauseButton").IsEnabled = active; Find<Button>(main, "PauseButton").Content = state == "paused" ? "Продолжить" : "Пауза";
   Find<Border>(main, "TimerCard").Visibility = active ? Visibility.Visible : Visibility.Collapsed;
   Find<Button>(main, "StartButton").Visibility = active ? Visibility.Collapsed : Visibility.Visible;
   UpdateStartButton();
   Find<UniformGrid>(main, "ActiveActions").Visibility = active ? Visibility.Visible : Visibility.Collapsed;
   Find<Button>(main, "WidgetButton").Visibility = active ? Visibility.Visible : Visibility.Collapsed;
   Find<UniformGrid>(main, "Presets").Visibility = !active && !showSchedule ? Visibility.Visible : Visibility.Collapsed;
   Find<StackPanel>(main, "ScheduleInputs").Visibility = !active && showSchedule ? Visibility.Visible : Visibility.Collapsed;
   Find<Button>(widget, "WidgetPause").Content = state == "running" ? "\uE769" : "\uE768";
   AutomationProperties.SetName(Find<Button>(widget, "WidgetPause"), state == "running" ? "Пауза" : active ? "Продолжить" : "Запустить");
   Find<Button>(widget, "WidgetCancel").IsEnabled = active;
   if (!active && widget.IsVisible) ShowMain();
   if (tray != null) {
    tray.Text = "Power Timer — " + (active ? (state == "paused" ? "пауза " : "осталось ") + time : "таймер не запущен");
    trayWidget.Available = trayPause.Available = trayCancel.Available = active;
    trayShutdown.Available = trayHibernate.Available = !active;
    trayPause.Text = state == "paused" ? "Продолжить" : "Пауза";
   }
  }
  void UpdateSleep() {
   bool hold = !test && prefs.keepAwake && engine.State == "running"; if (hold == sleepHeld) return;
   if (SetThreadExecutionState(Continuous | (hold ? SystemRequired : 0)) == 0) { Log("SetThreadExecutionState failed: " + Marshal.GetLastWin32Error()); return; } sleepHeld = hold;
  }
  void ExecutePowerAction(string action) {
   bool hibernate = action == TimerEngine.HibernateAction;
   string arguments = hibernate ? "/h" : "/s /t 0 /d p:0:0";
   if (test) {
    Log("TEST: would request shutdown " + arguments);
    return;
   }
   EnsureActionAvailable(action);
   Log(hibernate ? "Requesting Windows hibernation" : "Requesting graceful Windows shutdown");
   var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"), arguments) { UseShellExecute = false, CreateNoWindow = true };
   using (var process = Process.Start(start)) {
    if (!process.WaitForExit(5000)) throw new IOException("Команда не ответила.");
    if (process.ExitCode != 0) throw new IOException("Код Windows: " + process.ExitCode + ".");
   }
  }
  void Notify(string title, string text) { tray.BalloonTipTitle = title; tray.BalloonTipText = text; tray.BalloonTipIcon = Forms.ToolTipIcon.Info; tray.ShowBalloonTip(6000); }
  void RestoreUi() {
   if (Visible(custom)) { custom.Activate(); return; }
   if (Visible(settings)) { settings.WindowState = WindowState.Normal; settings.Activate(); return; }
   if (prefs.widgetMode && engine.Active) { ShowWidget(); return; }
   ShowMain();
  }
  void ShowMain() { CloseCustom(); SaveWidgetPosition(); widget.Hide(); prefs.widgetMode = false; main.Show(); main.WindowState = WindowState.Normal; main.Activate(); Persist(); }
  void ShowWidget() {
   if (!engine.Active) { ShowMain(); return; }
   CloseCustom(); main.Hide(); prefs.widgetMode = true; SetWidgetExpanded(false); if (!prefs.rememberWidgetPosition) PlaceWidget(false);
   widget.Show(); widget.UpdateLayout(); ClampWidget(); widget.Activate(); Persist();
  }
  void HideAll() { if (Visible(settings)) CancelSettings(); CloseCustom(); SaveWidgetPosition(); main.Hide(); widget.Hide(); Persist(); }
  void CloseRequested() { if (prefs.closeToTray) HideAll(); else RequestExit(); }
  void SetWidgetExpanded(bool expanded) {
   if (expanded && !widgetExpanded) widgetCompactAnchor = new Point(widget.Left, widget.Top);
   Point? restore = !expanded && widgetExpanded ? widgetCompactAnchor : null;
   widgetExpanded = expanded;
   Find<StackPanel>(widget, "WidgetControls").Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
   Find<Button>(widget, "WidgetExpandButton").Content = expanded ? "\uE76B" : "\uE76C";
   AutomationProperties.SetName(Find<Button>(widget, "WidgetExpandButton"), expanded ? "Скрыть управление" : "Показать управление");
   widget.UpdateLayout();
   if (restore.HasValue) { widget.Left = restore.Value.X; widget.Top = restore.Value.Y; }
   if (!expanded) widgetCompactAnchor = null;
   if (initialized) ClampWidget();
  }
  List<Rect> WorkAreas() {
   Matrix t = Matrix.Identity; var source = PresentationSource.FromVisual(widget) ?? PresentationSource.FromVisual(main);
   if (source != null && source.CompositionTarget != null) t = source.CompositionTarget.TransformFromDevice;
   else { double scale = SystemParameters.WorkArea.Width / Forms.Screen.PrimaryScreen.WorkingArea.Width; t = new Matrix(scale, 0, 0, scale, 0, 0); }
   var areas = new List<Rect>(); foreach (var screen in Forms.Screen.AllScreens) { var a = screen.WorkingArea; areas.Add(new Rect(t.Transform(new Point(a.Left, a.Top)), t.Transform(new Point(a.Right, a.Bottom)))); } return areas;
  }
  Size WidgetSize() { var root = Find<Border>(widget, "WidgetRoot"); root.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity)); return root.DesiredSize; }
  void PlaceWidget(bool restore) {
   Size size = WidgetSize();
   if (restore && prefs.rememberWidgetPosition && prefs.widgetX.HasValue && prefs.widgetY.HasValue) { widget.Left = prefs.widgetX.Value; widget.Top = prefs.widgetY.Value; }
   else { widget.Left = SystemParameters.WorkArea.Right - size.Width - 24; widget.Top = SystemParameters.WorkArea.Top + 24; } ClampWidget();
   if (widgetExpanded) widgetCompactAnchor = new Point(widget.Left, widget.Top);
  }
  void ClampWidget() {
   if (widget == null || Double.IsNaN(widget.Left) || Double.IsNaN(widget.Top)) return;
   Size size = WidgetSize(); Rect current = new Rect(widget.Left, widget.Top, size.Width, size.Height), best = SystemParameters.WorkArea; double overlap = 0;
   foreach (Rect a in WorkAreas()) { Rect r = Rect.Intersect(current, a); double amount = r.IsEmpty ? 0 : r.Width * r.Height; if (amount > overlap) { overlap = amount; best = a; } }
   double x = Math.Max(best.Left, Math.Min(widget.Left, best.Right - size.Width)), y = Math.Max(best.Top, Math.Min(widget.Top, best.Bottom - size.Height));
   if (Math.Abs(widget.Left - x) > .1) widget.Left = x; if (Math.Abs(widget.Top - y) > .1) widget.Top = y;
  }
  void SaveWidgetPosition() {
   if (prefs.rememberWidgetPosition && !Double.IsNaN(widget.Left) && !Double.IsNaN(widget.Top)) {
    prefs.widgetX = widgetExpanded && widgetCompactAnchor.HasValue ? widgetCompactAnchor.Value.X : widget.Left;
    prefs.widgetY = widgetExpanded && widgetCompactAnchor.HasValue ? widgetCompactAnchor.Value.Y : widget.Top;
   }
  }
  void ResetWidgetPosition() {
   prefs.widgetX = prefs.widgetY = null;
   PlaceWidget(false);
   Persist();
  }
  void Persist(bool timerChanged = false) {
   if (!initialized) return;
   try {
    SaveWidgetPosition();
    if (timerChanged) Store.WriteChanged(Path.Combine(folder, "timer.json"), engine.Export(), ref savedTimer);
    Store.WriteChanged(Path.Combine(folder, "settings.json"), prefs, ref savedSettings);
   }
   catch (Exception ex) { Log("Save failed: " + ex.Message); uiNotice = "Не удалось сохранить настройки: " + ex.Message; noticeUntil = clock.Elapsed + 15; ScheduleTick(); }
  }
  object Snapshot() {
   Size size = WidgetSize();
   return new { version = 1, appVersion = "2.6.1", state = engine.State, remainingSeconds = (int)Math.Ceiling(engine.Remaining), remaining = engine.Active ? TimerEngine.Format(engine.Remaining) : null,
    deadlineUtc = engine.Deadline.HasValue ? engine.Deadline.Value.ToString("o") : null, deadlineLocal = engine.Deadline.HasValue ? engine.Deadline.Value.ToLocalTime().ToString("o") : null,
    timingMode = engine.ScheduledUtc.HasValue ? "schedule" : "timer",
    totalSeconds = engine.Total, action = engine.State == "idle" ? selectedPowerAction : engine.ScheduledAction, testMode = test, ui = main.IsVisible ? "show" : widget.IsVisible ? "widget" : "tray", settingsOpen = Visible(settings), customOpen = Visible(custom), selectedSeconds = selectedSeconds, selectedIndex = selectedIndex,
    widgetExpanded = widgetExpanded, widgetWidth = size.Width, widgetHeight = size.Height, widgetLeft = widget.Left, widgetTop = widget.Top, widgetOpacity = widget.Opacity, widgetTopmost = widget.Topmost,
    effectiveTheme = effectiveTheme, keepAwake = prefs.keepAwake, sleepPreventionActive = sleepHeld, message = engine.Message };
  }
  static double Seconds(Dictionary<string, object> data) { object value; if (!data.TryGetValue("seconds", out value) || !(value is int || value is long || value is decimal || value is double)) throw new ApiError(400, "seconds must be a JSON number"); double n = Convert.ToDouble(value, CultureInfo.InvariantCulture); TimerEngine.Validate(n); return n; }
  object DispatchRequest(string method, string route, Dictionary<string, object> data) {
   var queued = Stopwatch.StartNew();
   return app.Dispatcher.Invoke(new Func<object>(delegate {
    if (queued.Elapsed.TotalSeconds > 3) throw new ApiError(409, "UI was busy; request was not applied");
    if (exiting || exitPending) throw new ApiError(409, "App is exiting");
    if (method == "GET" && route == "/v1/status") return Snapshot();
    if (method == "GET" && route == "/v1/settings") return SettingsSnapshot();
    if (test && method == "POST" && route == "/v1/test/render") return RenderTestWindows(data);
    if (test && method == "POST" && route == "/v1/test/interaction") return TestInteraction(data);
    if (test && method == "GET" && route == "/v1/test/ui") return InspectTestUi();
    if (method == "POST" && route == "/v1/settings") {
     if (Visible(settings)) throw new ApiError(409, "Settings are being edited"); CommitPreferences(Preferences.Patch(prefs, data)); return SettingsSnapshot();
    }
    if (method == "POST" && route == "/v1/schedule") {
     object target, action;
     if (!data.TryGetValue("at", out target) || !(target is string)) throw new ApiError(400, "at must be an ISO 8601 timestamp with a time zone");
     if (!data.TryGetValue("action", out action) || !TimerEngine.IsSupportedAction(action as string)) throw new ApiError(400, "action must be shutdown or hibernate");
     StartSchedule(ScheduleTime.ParseTimestamp((string)target), (string)action);
    }
    else if (method == "POST" && (route == "/v1/timer" || route == "/v1/timer/hibernate")) {
     string action = route == "/v1/timer" ? TimerEngine.ShutdownAction : TimerEngine.HibernateAction;
     object suppliedAction;
     if (data.TryGetValue("action", out suppliedAction) && !action.Equals(suppliedAction))
      throw new ApiError(400, "Action must match the endpoint. Use POST /v1/timer/hibernate for hibernation.");
     StartTimer(Seconds(data), action);
    }
    else if (method == "DELETE" && route == "/v1/timer") engine.Cancel();
    else if (method == "POST" && route == "/v1/pause") engine.Pause();
    else if (method == "POST" && route == "/v1/resume") ResumeTimer();
    else if (method == "POST" && route == "/v1/extend") engine.Extend(Seconds(data));
    else if (method == "POST" && route == "/v1/widget") { object value; if (!data.TryGetValue("expanded", out value) || !(value is bool)) throw new ApiError(400, "expanded must be a boolean"); if (!widget.IsVisible) ShowWidget(); SetWidgetExpanded((bool)value); }
    else if (method == "POST" && route == "/v1/widget/reset") ResetWidgetPosition();
    else if (method == "POST" && route == "/v1/ui") {
     object value; if (!data.TryGetValue("mode", out value) || !(value is string)) throw new ApiError(400, "mode must be show, widget, settings or tray");
     string mode = (string)value; if (mode == "restore") RestoreUi(); else if (mode == "show") ShowMain(); else if (mode == "widget") ShowWidget(); else if (mode == "settings") OpenSettings(); else if (mode == "tray") HideAll(); else throw new ApiError(400, "Unknown UI mode");
    }
    else if (method == "POST" && route == "/v1/app/exit") RequestExit(); else throw new ApiError(404, "Unknown API route"); return Snapshot();
   }));
  }
  void RequestExit() {
   if (exiting || exitPending) return; if (Visible(settings)) CancelSettings(); engine.Cancel(); exitPending = true;
   var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) }; delay.Tick += delegate { delay.Stop(); Exit(); }; delay.Start();
  }
  void Exit() { if (exiting) return; engine.Cancel(); exiting = true; ticker.Stop(); SetThreadExecutionState(Continuous); sleepHeld = false; Persist(); app.Shutdown(); }
  public void Log(string text) { try { string file = Path.Combine(folder, "activity.log"); if (File.Exists(file) && new FileInfo(file).Length > 262144) File.Delete(file); File.AppendAllText(file, DateTime.UtcNow.ToString("o") + " " + text + Environment.NewLine); } catch { } }
  public void Dispose() {
   scheduleTextProperty.RemoveValueChanged(scheduleClock, ScheduleClockChanged);
   SystemEvents.UserPreferenceChanged -= OnSystemPreferences; SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged; SystemEvents.TimeChanged -= OnTimeChanged;
   if (api != null) api.Dispose(); if (ticker != null) ticker.Stop(); SetThreadExecutionState(Continuous); if (tray != null) { tray.Visible = false; if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose(); tray.Dispose(); } if (trayIcon != null) trayIcon.Dispose();
   try { File.Delete(Path.Combine(folder, "api.json")); } catch { } Log("Stopped");
  }
 }
}
