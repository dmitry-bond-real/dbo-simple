using System;
using System.Diagnostics;
using System.Net;
using System.Threading;

namespace Simple.HttpServer
{
    /// <summary>
    /// Engine based on <see cref="System.Net.HttpListener"/> (HTTP.SYS). Default/preferred engine — gets
    /// HTTP/1.1 protocol correctness (keep-alive, chunked requests, etc.) for free from the OS.
    /// </summary>
    internal sealed class HttpListenerEngine : IHttpEngine
    {
        public static TraceSwitch TrcLvl => SimpleHttpServer.TrcLvl;

        private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);

        private readonly SimpleHttpServerSettings _settings;
        private readonly Action<IHttpContext> _dispatch;
        private HttpListener _listener;
        private volatile bool _running;
        private int _activeRequests;

        public HttpListenerEngine(SimpleHttpServerSettings pSettings, Action<IHttpContext> pDispatch)
        {
            _settings = pSettings;
            _dispatch = pDispatch;
        }

        public bool IsRunning => _running;

        public void Start()
        {
            _listener = CreateAndStartListener();
            _running = true;

            Trace.WriteLineIf(SimpleHttpServer.TrcLvl.TraceInfo,
                "HttpListenerEngine: listening on port " + _settings.Port);

            BeginGetContext();
        }

        // Wildcard binding requires an admin-elevated URL-ACL reservation (see specs.md §2.1); when that is
        // denied, fall back to a loopback-only prefix, which HTTP.SYS exempts from the reservation requirement.
        private HttpListener CreateAndStartListener()
        {
            var listener = new HttpListener();
            listener.Prefixes.Add("http://+:" + _settings.Port + "/");
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                Trace.WriteLineIf(SimpleHttpServer.TrcLvl.TraceWarning,
                    "HttpListenerEngine: wildcard bind denied (" + ex.Message + "); retrying on 'localhost' only.");

                listener = new HttpListener();
                listener.Prefixes.Add("http://localhost:" + _settings.Port + "/");
                listener.Start();
                return listener;
            }
        }

        private void BeginGetContext()
        {
            try
            {
                _listener.BeginGetContext(GetContextCallback, null);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void GetContextCallback(IAsyncResult pResult)
        {
            HttpListenerContext httpContext = null;
            try
            {
                httpContext = _listener.EndGetContext(pResult);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (HttpListenerException ex)
            {
                Trace.WriteLineIf(SimpleHttpServer.TrcLvl.TraceWarning, "HttpListenerEngine: EndGetContext failed: " + ex.Message);
            }

            if (_running) BeginGetContext();

            if (httpContext == null) return;

            Interlocked.Increment(ref _activeRequests);
            ThreadPool.QueueUserWorkItem(ProcessContext, httpContext);
        }

        private void ProcessContext(object pState)
        {
            var httpContext = (HttpListenerContext)pState;
            Trace.WriteLineIf(TrcLvl.TraceInfo, TrcLvl.TraceInfo ? string.Format("--> StdHttp[{0}].prcCtx( {1} )...", this._settings.Port, httpContext.Request.RawUrl) : "");
            try
            {
                var ctx = new HttpListenerContextAdapter(httpContext, _settings);
                _dispatch(ctx);
                if (!ctx.WasSent) ctx.Flush();
            }
            catch (Exception ex)
            {
                Trace.WriteLineIf(SimpleHttpServer.TrcLvl.TraceError, "HttpListenerEngine: request processing error: " + ex);
                try { httpContext.Response.Close(); } catch (Exception) { }
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            try { _listener?.Stop(); } catch (Exception) { }

            var sw = Stopwatch.StartNew();
            while (Volatile.Read(ref _activeRequests) > 0 && sw.Elapsed < ShutdownWait)
                Thread.Sleep(25);

            try { _listener?.Close(); } catch (Exception) { }
        }

        public void Dispose() => Stop();
    }
}
