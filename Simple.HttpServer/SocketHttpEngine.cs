using Simple.HttpServer.Alt;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Simple.HttpServer
{
    /// <summary>
    /// Engine based on a raw <see cref="TcpListener"/> with hand-parsed HTTP/1.1. Used when HTTP.SYS is
    /// unavailable/restricted (see Simple.HttpServer.specs.md §2.1). Implements its own keep-alive handling
    /// and enforces request-line/header size limits since no HTTP.SYS driver does it for free here.
    /// </summary>
    internal sealed class SocketHttpEngine : IHttpEngine
    {
        public static TraceSwitch TrcLvl { get { return SimpleHttpServer.TrcLvl; } }

        private const int MaxRequestLineLength = 8 * 1024;
        private const int MaxHeaderLineLength = 8 * 1024;
        private const int MaxHeaderCount = 100;
        private const long MaxBodyLength = 10 * 1024 * 1024;
        private const int MaxRequestsPerConnection = 200;
        private const int ConnectionTimeoutMs = 30000;
        private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);

        private enum ReadResult { Ok, ConnectionClosed, Malformed }

        private readonly SimpleHttpServerSettings _settings;
        private readonly Action<IHttpContext> _dispatch;
        private TcpListener _listener;
        private volatile bool _running;
        private int _activeConnections;

        public SocketHttpEngine(SimpleHttpServerSettings pSettings, Action<IHttpContext> pDispatch)
        {
            _settings = pSettings;
            _dispatch = pDispatch;
        }

        public bool IsRunning => _running;

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Any, _settings.Port);
            int backlog = _settings.ConnectQueueLength > 0 ? _settings.ConnectQueueLength : 100;
            _listener.Start(backlog);
            _running = true;

            Trace.WriteLineIf(TrcLvl.TraceInfo, string.Format("SockHttp: listening on port {0}", _settings.Port));

            BeginAccept();
        }

        private void BeginAccept()
        {
            try
            {
                _listener.BeginAcceptTcpClient(AcceptCallback, null);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void AcceptCallback(IAsyncResult pResult)
        {
            TcpClient client = null;
            try
            {
                client = _listener.EndAcceptTcpClient(pResult);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                Trace.WriteLineIf(TrcLvl.TraceWarning, "SockHttp." + Utils.FormatErrorMsg(ex));
            }

            if (_running) BeginAccept();

            if (client == null) return;

            Interlocked.Increment(ref _activeConnections);
            ThreadPool.QueueUserWorkItem(HandleConnection, client);
        }

        private void HandleConnection(object pState)
        {
            var client = (TcpClient)pState;
            try
            {
                client.ReceiveTimeout = ConnectionTimeoutMs;
                client.SendTimeout = ConnectionTimeoutMs;

                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    int requestCount = 0;
                    while (_running && requestCount < MaxRequestsPerConnection)
                    {
                        requestCount++;

                        ReadResult result = TryReadRequest(stream, client, out SocketHttpContext ctx, out bool keepAliveRequested);
                        if (result == ReadResult.ConnectionClosed) break;
                        if (result == ReadResult.Malformed)
                        {
                            WriteRawError(stream, 400, "Bad Request");
                            break;
                        }

                        try
                        {
                            Trace.WriteLineIf(TrcLvl.TraceInfo, TrcLvl.TraceInfo ? string.Format(
                                "--> SockHttp[{0}]: {1}", this._settings.Port, ctx.Url) : "");

                            _dispatch(ctx);
                            if (!ctx.WasSent) ctx.Flush();
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLineIf(TrcLvl.TraceError, "SockHttp." + Utils.FormatErrorMsg(ex));
                        }

                        if (!keepAliveRequested || ctx.ConnectionCloseRequested) break;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLineIf(TrcLvl.TraceWarning, "SockHttp." + Utils.FormatErrorMsg(ex));
            }
            finally
            {
                Interlocked.Decrement(ref _activeConnections);
            }
        }

        private static bool IsLocalEndPoint(TcpClient pClient)
        {
            try
            {
                var remote = pClient.Client.RemoteEndPoint as IPEndPoint;
                var local = pClient.Client.LocalEndPoint as IPEndPoint;
                if (remote == null) return false;
                return IPAddress.IsLoopback(remote.Address) || (local != null && remote.Address.Equals(local.Address));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private ReadResult TryReadRequest(Stream pStream, TcpClient pClient, out SocketHttpContext pContext, out bool pKeepAlive)
        {
            pContext = null;
            pKeepAlive = false;

            try
            {
                string requestLine = ReadLine(pStream, MaxRequestLineLength);
                if (requestLine == null) return ReadResult.ConnectionClosed;
                if (requestLine.Length == 0) return ReadResult.Malformed;

                string[] parts = requestLine.Split(new[] { ' ' }, 3);
                if (parts.Length != 3) return ReadResult.Malformed;

                string method = parts[0];
                string target = parts[1];
                string version = parts[2];
                if (!version.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase)) return ReadResult.Malformed;

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; ; i++)
                {
                    string line = ReadLine(pStream, MaxHeaderLineLength);
                    if (line == null) return ReadResult.ConnectionClosed;
                    if (line.Length == 0) break;
                    if (i >= MaxHeaderCount) return ReadResult.Malformed;

                    int colon = line.IndexOf(':');
                    if (colon <= 0) return ReadResult.Malformed;
                    string name = line.Substring(0, colon).Trim();
                    string value = line.Substring(colon + 1).Trim();
                    headers[name] = value;
                }

                byte[] body = Array.Empty<byte>();
                if (headers.TryGetValue("Content-Length", out string clValue) &&
                    long.TryParse(clValue, out long contentLength) && contentLength > 0)
                {
                    if (contentLength > MaxBodyLength) return ReadResult.Malformed;
                    body = ReadExact(pStream, (int)contentLength);
                    if (body == null) return ReadResult.ConnectionClosed;
                }

                bool isHttp11 = version.Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase);
                headers.TryGetValue("Connection", out string connectionHeader);
                bool explicitClose = connectionHeader != null && connectionHeader.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0;
                bool explicitKeepAlive = connectionHeader != null && connectionHeader.IndexOf("keep-alive", StringComparison.OrdinalIgnoreCase) >= 0;
                pKeepAlive = isHttp11 ? !explicitClose : explicitKeepAlive;

                bool isLocal = IsLocalEndPoint(pClient);
                pContext = new SocketHttpContext(_settings, method, target, headers, body, pStream, isLocal);
                return ReadResult.Ok;
            }
            catch (InvalidOperationException)
            {
                return ReadResult.Malformed; // request-line/header exceeded max length
            }
        }

        private static string ReadLine(Stream pStream, int pMaxLength)
        {
            var buffer = new List<byte>(128);
            int prev = -1;
            while (true)
            {
                int b = pStream.ReadByte();
                if (b < 0) return buffer.Count == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray());

                if (prev == '\r' && b == '\n')
                {
                    buffer.RemoveAt(buffer.Count - 1);
                    return Encoding.ASCII.GetString(buffer.ToArray());
                }

                buffer.Add((byte)b);
                if (buffer.Count > pMaxLength) throw new InvalidOperationException("Request line/header too long");
                prev = b;
            }
        }

        private static byte[] ReadExact(Stream pStream, int pCount)
        {
            var buffer = new byte[pCount];
            int offset = 0;
            while (offset < pCount)
            {
                int read = pStream.Read(buffer, offset, pCount - offset);
                if (read <= 0) return null;
                offset += read;
            }
            return buffer;
        }

        private static void WriteRawError(Stream pStream, int pCode, string pReason)
        {
            try
            {
                string body = "<html><body><h1>" + pCode + " " + pReason + "</h1></body></html>";
                byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                string headers = "HTTP/1.1 " + pCode + " " + pReason + "\r\n"
                    + "Content-Type: text/html\r\n"
                    + "Content-Length: " + bodyBytes.Length + "\r\n"
                    + "Connection: close\r\n\r\n";
                byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
                pStream.Write(headerBytes, 0, headerBytes.Length);
                pStream.Write(bodyBytes, 0, bodyBytes.Length);
                pStream.Flush();
            }
            catch (Exception) { }
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            try { _listener?.Stop(); } catch (Exception) { }

            var sw = Stopwatch.StartNew();
            while (Volatile.Read(ref _activeConnections) > 0 && sw.Elapsed < ShutdownWait)
                Thread.Sleep(25);
        }

        public void Dispose() => Stop();
    }
}
