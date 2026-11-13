using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace PowerTimer {
    public static class ApiClient {
        public static string DataPath(bool test) {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), test ? "PowerTimer-Test" : "PowerTimer");
        }
        public static string Send(bool test, string method, string route, object body) {
            return Send(test, method, route, body, 5000);
        }
        public static string Send(bool test, string method, string route, object body, int timeoutMilliseconds) {
            var json = new JavaScriptSerializer();
            string path = Path.Combine(DataPath(test), "api.json");
            if (!File.Exists(path)) throw new IOException("PowerTimer не запущен. Откройте PowerTimer.exe.");
            var info = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            string baseUrl = Convert.ToString(info["baseUrl"]);
            Uri uri;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "")
                throw new IOException("Некорректный локальный адрес API.");
            var request = (HttpWebRequest)WebRequest.Create(baseUrl + route);
            request.Method = method; request.Proxy = null; request.Timeout = timeoutMilliseconds;
            request.ReadWriteTimeout = timeoutMilliseconds; request.AllowAutoRedirect = false;
            request.Headers["Authorization"] = "Bearer " + Convert.ToString(info["token"]);
            request.KeepAlive = false;
            if (body != null) {
                byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(body));
                request.ContentType = "application/json"; request.ContentLength = bytes.Length;
                using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            } else { request.ContentLength = 0; }
            try {
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream())) return reader.ReadToEnd();
            } catch (WebException ex) {
                if (ex.Response == null) throw new IOException("Нет связи с PowerTimer. Запустите приложение.", ex);
                using (var response = ex.Response)
                using (var reader = new StreamReader(response.GetResponseStream())) throw new IOException(reader.ReadToEnd(), ex);
            }
        }
    }
    public static class ControlCli {
        public static int Main(string[] args) {
            Console.OutputEncoding = new UTF8Encoding(false);
            try {
                bool test = Array.IndexOf(args, "--test") >= 0;
                var list = new List<string>(args); list.Remove("--test"); args = list.ToArray();
                if (args.Length == 0 || args[0] == "help" || args[0] == "--help") {
                    Console.WriteLine("timerctl status | after 30m | hibernate 30m | at ISO_TIMESTAMP [shutdown|hibernate] | cancel | pause | resume | add 10m | show | widget | tray | settings | config | exit\nDuration: number + s/m/h (or seconds). The timestamp needs Z or a UTC offset. --test connects only to the harmless test instance.\nCommands return JSON; exit code 0 = success, 1 = error. 'exit' cancels the timer and closes the app."); return 0;
                }
                string method = "POST", route; object body = new { };
                switch (args[0]) {
                    case "status": method = "GET"; route = "v1/status"; body = null; break;
                    case "at":
                        if (args.Length < 2 || args.Length > 3)
                            throw new ArgumentException("Укажите дату и время с часовым поясом и, при необходимости, действие shutdown или hibernate.");
                        route = "v1/schedule";
                        body = new { at = args[1], action = args.Length == 3 ? args[2] : "shutdown" };
                        break;
                    case "after": case "hibernate": case "add":
                        if (args.Length != 2) throw new ArgumentException("Укажите длительность, например " + args[0] + " 30m.");
                        double seconds = ParseDuration(args[1]);
                        if (args[0] == "add") route = "v1/extend";
                        else route = args[0] == "hibernate" ? "v1/timer/hibernate" : "v1/timer";
                        body = new { seconds = seconds }; break;
                    case "cancel": method = "DELETE"; route = "v1/timer"; body = null; break;
                    case "pause": case "resume": route = "v1/" + args[0]; break;
                    case "show": case "widget": case "tray": case "settings": route = "v1/ui"; body = new { mode = args[0] }; break;
                    case "config": method = "GET"; route = "v1/settings"; body = null; break;
                    case "exit": route = "v1/app/exit"; break;
                    default: throw new ArgumentException("Неизвестная команда. Используйте timerctl help.");
                }
                Console.WriteLine(ApiClient.Send(test, method, route, body)); return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        public static double ParseDuration(string value) {
            value = value.Trim().ToLowerInvariant(); double multiplier = 1;
            if (value.EndsWith("s")) value = value.Substring(0, value.Length - 1);
            else if (value.EndsWith("m")) { multiplier = 60; value = value.Substring(0, value.Length - 1); }
            else if (value.EndsWith("h")) { multiplier = 3600; value = value.Substring(0, value.Length - 1); }
            double result;
            if (!Double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result) || Double.IsNaN(result) || Double.IsInfinity(result) || result * multiplier < 1 || result * multiplier > 604800)
                throw new ArgumentException("Длительность: от 1 секунды до 7 суток, например 15m, 2h, 90s.");
            return result * multiplier;
        }
    }
}
