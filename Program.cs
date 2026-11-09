using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
[assembly: AssemblyTitle("Power Timer")]
[assembly: AssemblyDescription("Таймер выключения и гибернации с виджетом и локальным API")]
[assembly: AssemblyVersion("2.4.0.0")]
[assembly: AssemblyFileVersion("2.4.0.0")]
namespace PowerTimer {
 public static class Program {
  [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
  static int ForwardToExisting(bool test, bool startup, string[] args) {
   if (startup) return 0;
   string mode = Array.IndexOf(args, "--widget") >= 0 ? "widget" : Array.IndexOf(args, "--tray") >= 0 ? "tray" : "restore";
   Exception last = null;
   var deadline = Stopwatch.StartNew();
   while (deadline.ElapsedMilliseconds < 8000) {
    try {
     var info = Store.Read<Dictionary<string, object>>(Path.Combine(ApiClient.DataPath(test), "api.json"));
     if (info != null && info.ContainsKey("pid")) AllowSetForegroundWindow(Convert.ToInt32(info["pid"]));
     ApiClient.Send(test, "POST", "v1/ui", new { mode = mode }, 1000); return 0;
    } catch (Exception ex) { last = ex; Thread.Sleep(100); }
   }
   MessageBox.Show(last == null ? "Приложение уже запущено." : last.Message, "Power Timer"); return 1;
  }
  [STAThread] public static int Main(string[] args) {
   bool test = Array.IndexOf(args, "--test") >= 0, startup = Array.IndexOf(args, "--startup") >= 0, created;
   Mutex mutex = null; FileStream instanceLock = null; string path = ApiClient.DataPath(test);
   try {
    Store.SecureDirectory(path);
    try { instanceLock = new FileStream(Path.Combine(path, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    catch (IOException ex) {
     int code = Marshal.GetHRForException(ex) & 0xFFFF;
     if (code != 32 && code != 33) throw;
     return ForwardToExisting(test, startup, args);
    }
    mutex = new Mutex(true, "Local\\Roman.PowerTimer." + WindowsIdentity.GetCurrent().User.Value + (test ? ".Test" : ""), out created);
    if (!created) return ForwardToExisting(test, startup, args);
    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
    using (var host = new TimerApplication(app, path, test)) {
     app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e) { host.Log("UI error: " + e.Exception); host.ShowError(e.Exception.Message); e.Handled = true; };
     host.Start(Array.IndexOf(args, "--tray") >= 0, Array.IndexOf(args, "--widget") >= 0, startup); app.Run();
    }
    return 0;
   } catch (Exception ex) {
    try { Directory.CreateDirectory(path); File.AppendAllText(Path.Combine(path, "error.log"), DateTime.UtcNow.ToString("o") + " " + ex + Environment.NewLine); } catch { }
    MessageBox.Show("Не удалось запустить таймер: " + ex.Message, "Power Timer", MessageBoxButton.OK, MessageBoxImage.Error); return 1;
   } finally { if (mutex != null) mutex.Dispose(); if (instanceLock != null) instanceLock.Dispose(); }
  }
 }
}
