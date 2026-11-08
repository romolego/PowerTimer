using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace PowerTimer {
    public sealed class ApiError : Exception {
        public int Status;
        public ApiError(int status, string message) : base(message) { Status = status; }
    }
    public sealed class LocalApi : IDisposable {
        readonly TcpListener listener;
        readonly string token;
        readonly Func<string, string, Dictionary<string, object>, object> handle;
        readonly object clientLock = new object();
        readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
        volatile bool stopped;
        public int Port { get; private set; }
        public LocalApi(string token, Func<string, string, Dictionary<string, object>, object> handle, int preferredPort) {
            this.token = token; this.handle = handle;
            listener = new TcpListener(IPAddress.Loopback, preferredPort);
            listener.Server.ExclusiveAddressUse = true;
            try { listener.Start(8); }
            catch (SocketException) {
                listener.Stop(); listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Server.ExclusiveAddressUse = true; listener.Start(8);
            }
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            new Thread(Accept) { IsBackground = true, Name = "PowerTimer API" }.Start();
        }
        void Accept() {
            while (!stopped) {
                try {
                    TcpClient client = listener.AcceptTcpClient();
                    client.ReceiveTimeout = 2500; client.SendTimeout = 2500;
                    bool accepted;
                    lock (clientLock) {
                        if (stopped) { client.Close(); return; }
                        accepted = clients.Count < 8;
                        if (accepted) clients.Add(client);
                    }
                    if (!accepted) {
                        try { Send(client.GetStream(), 503, new { error = "API is busy; retry shortly" }); } catch { }
                        CloseClient(client); continue;
                    }
                    ThreadPool.QueueUserWorkItem(delegate {
                        try { if (!stopped) Serve(client); } catch { }
                        finally { CloseClient(client); lock (clientLock) clients.Remove(client); }
                    });
                } catch (SocketException) { if (stopped) return; Thread.Sleep(50); }
                  catch (ObjectDisposedException) { return; }
            }
        }
        static bool Equal(string a, string b) {
            if (a == null || a.Length != b.Length) return false;
            int diff = 0; for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i]; return diff == 0;
        }
        static void CloseClient(TcpClient client) {
            Socket socket = client.Client;
            if (socket == null) return;
            try {
                // Send FIN before draining any rejected request bytes. Closing directly
                // with unread bytes causes Windows to reset the socket and can erase an
                // otherwise complete JSON error response at the peer.
                socket.Shutdown(SocketShutdown.Send);
                client.ReceiveTimeout = 100;
                byte[] discard = new byte[1024]; int consumed = 0;
                var elapsed = Stopwatch.StartNew();
                while (consumed < 32768 && elapsed.ElapsedMilliseconds < 100) {
                    int count = socket.Receive(discard);
                    if (count == 0) break;
                    consumed += count;
                }
            } catch (SocketException) { }
              catch (ObjectDisposedException) { }
              finally { client.Close(); }
        }
        static void SetReadDeadline(NetworkStream stream, Stopwatch elapsed) {
            int remaining = 5000 - (int)elapsed.ElapsedMilliseconds;
            if (remaining <= 0) throw new ApiError(408, "Request timed out");
            if (remaining < 2500) stream.ReadTimeout = remaining;
        }
        // Buffer socket reads instead of making a kernel call per header byte.
        // The total request deadline and header/body size limits still apply.
        sealed class RequestReader {
            readonly NetworkStream stream;
            readonly Stopwatch deadline;
            readonly byte[] buffer = new byte[4096];
            int position, available;
            public RequestReader(NetworkStream stream, Stopwatch deadline) { this.stream = stream; this.deadline = deadline; }
            bool Fill() {
                if (position < available) return true;
                SetReadDeadline(stream, deadline);
                available = stream.Read(buffer, 0, buffer.Length); position = 0;
                return available > 0;
            }
            public int ReadByte() { return Fill() ? buffer[position++] : -1; }
            public int Read(byte[] target, int offset, int count) {
                if (!Fill()) return 0;
                int length = Math.Min(count, available - position);
                Buffer.BlockCopy(buffer, position, target, offset, length); position += length;
                return length;
            }
        }
        void Serve(TcpClient client) {
            client.ReceiveTimeout = 2500; client.SendTimeout = 2500;
            NetworkStream stream = client.GetStream();
            try {
                var deadline = Stopwatch.StartNew();
                var reader = new RequestReader(stream, deadline);
                List<byte> head = new List<byte>(); int b;
                while (true) {
                    b = reader.ReadByte();
                    if (b < 0) return;
                    head.Add((byte)b); int n = head.Count;
                    if (n > 8192) throw new ApiError(413, "Headers too large");
                    if (n >= 4 && head[n-4] == 13 && head[n-3] == 10 && head[n-2] == 13 && head[n-1] == 10) break;
                }
                string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split(new string[] { "\r\n" }, StringSplitOptions.None);
                string[] request = lines[0].Split(' ');
                if (request.Length != 3 || request[2] != "HTTP/1.1") throw new ApiError(400, "Invalid HTTP request");
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++) {
                    if (lines[i].Length == 0) continue;
                    int colon = lines[i].IndexOf(':');
                    if (colon <= 0) throw new ApiError(400, "Invalid header");
                    string key = lines[i].Substring(0, colon).Trim();
                    if (headers.ContainsKey(key)) throw new ApiError(400, "Duplicate header");
                    headers[key] = lines[i].Substring(colon + 1).Trim();
                }
                string value;
                if (headers.ContainsKey("Transfer-Encoding")) throw new ApiError(400, "Chunked requests are not supported");
                int length = 0;
                if (headers.TryGetValue("Content-Length", out value) && (!Int32.TryParse(value, out length) || length < 0 || length > 16384))
                    throw new ApiError(413, "Invalid body length");
                byte[] body = new byte[length]; int read = 0;
                while (read < length) {
                    int count = reader.Read(body, read, length - read); if (count == 0) throw new ApiError(400, "Incomplete body"); read += count;
                }
                // Consume a bounded body before rejecting the request, so closing the socket
                // cannot discard the JSON error response through a Windows TCP reset.
                if (!headers.TryGetValue("Host", out value) || value != "127.0.0.1:" + Port)
                    throw new ApiError(403, "Use the loopback address from api.json");
                if (headers.ContainsKey("Origin")) throw new ApiError(403, "Browser origins are not allowed");
                if (!headers.TryGetValue("Authorization", out value) || !Equal(value, "Bearer " + token))
                    throw new ApiError(401, "Bearer token required");
                if (request[0] == "POST" && (!headers.TryGetValue("Content-Type", out value) || !String.Equals(value.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase)))
                    throw new ApiError(415, "Use application/json");
                Dictionary<string, object> data = new Dictionary<string, object>();
                if (length > 0) {
                    try { data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(new UTF8Encoding(false, true).GetString(body)); }
                    catch { throw new ApiError(400, "Invalid JSON object"); }
                    if (data == null) throw new ApiError(400, "Invalid JSON object");
                }
                if (stopped) throw new ApiError(503, "API is stopping");
                if (deadline.ElapsedMilliseconds > 5000) throw new ApiError(408, "Request timed out");
                object response = handle(request[0], request[1], data);
                Send(stream, 200, response);
            } catch (ApiError ex) { Send(stream, ex.Status, new { error = ex.Message }); }
              catch (ArgumentException ex) { Send(stream, 400, new { error = ex.Message }); }
              catch (InvalidOperationException ex) { Send(stream, 409, new { error = ex.Message }); }
              catch (IOException ex) {
                  var socketError = ex.InnerException as SocketException;
                  bool timedOut = socketError != null && socketError.SocketErrorCode == SocketError.TimedOut;
                  Send(stream, timedOut ? 408 : 500, new { error = timedOut ? "Request timed out" : "I/O operation failed" });
              }
              catch (Exception) { Send(stream, 500, new { error = "Internal error; see the app" }); }
        }
        static void Send(Stream stream, int status, object data) {
            byte[] body = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(data));
            string reason = status == 200 ? "OK" : status == 400 ? "Bad Request" : status == 401 ? "Unauthorized" : status == 403 ? "Forbidden" : status == 404 ? "Not Found" : status == 408 ? "Request Timeout" : status == 409 ? "Conflict" : status == 413 ? "Payload Too Large" : status == 415 ? "Unsupported Media Type" : status == 503 ? "Service Unavailable" : "Internal Server Error";
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + reason + "\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n\r\n");
            byte[] packet = new byte[header.Length + body.Length];
            Buffer.BlockCopy(header, 0, packet, 0, header.Length); Buffer.BlockCopy(body, 0, packet, header.Length, body.Length);
            stream.Write(packet, 0, packet.Length);
        }
        public void Dispose() {
            stopped = true; listener.Stop();
            // Release clients blocked in reads immediately; no stale request can survive
            // an API restart. Worker callbacks remove themselves from the client set.
            lock (clientLock) foreach (TcpClient client in clients) client.Close();
        }
    }
}
