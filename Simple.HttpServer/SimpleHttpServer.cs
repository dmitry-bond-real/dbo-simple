using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Simple.HttpServer
{
    /// <summary>
    /// Lightweight embeddable HTTP server. Thin façade over one of two interchangeable transport engines
    /// (<see cref="HttpServerEngineKind.Socket"/> or <see cref="HttpServerEngineKind.HttpListener"/>) selected
    /// via <see cref="SimpleHttpServerSettings.EngineKind"/>; see Simple.HttpServer.specs.md §2.1/§2.2.
    /// </summary>
    public class SimpleHttpServer : IDisposable
    {
        /// <summary>Trace switch "SimpleHttpServer"; configurable via &lt;system.diagnostics&gt;&lt;switches&gt;.</summary>
        public static readonly TraceSwitch TrcLvl = new TraceSwitch("SimpleHttpServer", "Simple.HttpServer trace switch");

        /// <summary>Default app.config section name used by the parameterless constructor.</summary>
        public const string DefaultConfigSectionName = "SimpleHttpServer";

        /// <summary>Prototype of a method that handles a request for a registered URL-prefix.</summary>
        public delegate bool HandleServiceRequestMethod(IHttpContext pCtx);

        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, HandleServiceRequestMethod> _handlers = new Dictionary<string, HandleServiceRequestMethod>(StringComparer.OrdinalIgnoreCase);
        private readonly HttpServerEngineKind? _explicitEngine;
        private IHttpEngine _engine;

        public SimpleHttpServer() : this(DefaultConfigSectionName) { }

        public SimpleHttpServer(string pConfigSectionName)
        {
            ConfigSectionName = pConfigSectionName ?? string.Empty;
            Settings = string.IsNullOrEmpty(pConfigSectionName)
                ? SimpleHttpServerSettings.CreateDefault()
                : SimpleHttpServerSettings.LoadFromConfig(pConfigSectionName);
        }

        public SimpleHttpServer(SimpleHttpServerSettings pSettings)
        {
            ConfigSectionName = string.Empty;
            Settings = pSettings ?? SimpleHttpServerSettings.CreateDefault();
        }

        /// <summary>Explicit-override constructor: bypasses <see cref="SimpleHttpServerSettings.EngineKind"/> auto-resolution.</summary>
        public SimpleHttpServer(SimpleHttpServerSettings pSettings, HttpServerEngineKind pEngine) : this(pSettings)
        {
            _explicitEngine = pEngine;
        }

        /// <summary>Name of the app.config section this instance was configured from (empty if constructed programmatically).</summary>
        public string ConfigSectionName { get; }

        /// <summary>Effective settings for this server instance.</summary>
        public SimpleHttpServerSettings Settings { get; }

        /// <summary>True while the server is accepting/processing requests.</summary>
        public bool IsRunning => _engine?.IsRunning ?? false;

        /// <summary>The engine actually started (Auto gets resolved to a concrete engine); undefined while stopped.</summary>
        public HttpServerEngineKind ActiveEngine { get; private set; }

        /// <summary>HTTP methods accepted by the server; unlisted methods get 405.</summary>
        public List<string> AllowedMethods { get; } = new List<string> { "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS" };

        /// <summary>Synchronization object guarding mutable shared state (handlers, lifecycle).</summary>
        public object SyncRoot => _syncRoot;

        /// <summary>Fallback handler invoked when no registered URL-prefix handler matches the request.</summary>
        public HandleServiceRequestMethod DefaultServiceRequestHandler { get; set; }

        /// <summary>
        /// Registers (or replaces) the handler for a URL-prefix. Requests are routed to the
        /// longest-matching registered prefix (case-insensitive).
        /// </summary>
        public void AddServiceRequestHandler(string pUrlBase, HandleServiceRequestMethod pHandler)
        {
            if (string.IsNullOrEmpty(pUrlBase)) throw new ArgumentNullException(nameof(pUrlBase));
            if (pHandler == null) throw new ArgumentNullException(nameof(pHandler));

            lock (_syncRoot)
            {
                _handlers[pUrlBase] = pHandler;
            }
        }

        /// <summary>Removes a previously registered handler (matched by delegate reference, regardless of its prefix).</summary>
        public void RemoveServiceRequestHandler(HandleServiceRequestMethod pHandler)
        {
            lock (_syncRoot)
            {
                foreach (string key in _handlers.Where(kv => kv.Value == pHandler).Select(kv => kv.Key).ToList())
                    _handlers.Remove(key);
            }
        }

        /// <summary>Starts the server. No-op if already running or if <see cref="SimpleHttpServerSettings.Enabled"/> is false.</summary>
        public void Start()
        {
            lock (_syncRoot)
            {
                if (IsRunning) return;
                if (!Settings.Enabled) return;

                HttpServerEngineKind kind = _explicitEngine ?? Settings.EngineKind;
                if (kind == HttpServerEngineKind.Auto) kind = HttpServerEngineKind.HttpListener;

                IHttpEngine engine;
                try
                {
                    engine = CreateEngine(kind);
                    engine.Start();
                }
                catch (Exception ex)
                {
                    Trace.WriteLineIf(SimpleHttpServer.TrcLvl.TraceWarning, "SimpleHttpServer: failed to start engine " + kind + ": " + ex.Message);

                    if (kind == HttpServerEngineKind.HttpListener && Settings.AutoFallbackToSocketEngine)
                    {
                        kind = HttpServerEngineKind.Socket;
                        engine = CreateEngine(kind);
                        engine.Start();
                    }
                    else
                    {
                        throw;
                    }
                }

                _engine = engine;
                ActiveEngine = kind;

                Trace.WriteLineIf(SimpleHttpServer.TrcLvl.TraceInfo, "SimpleHttpServer: started (engine=" + kind + ", port=" + Settings.Port + ")");
            }
        }

        /// <summary>Stops the server. No-op if not running. Safe to call from <see cref="Dispose"/>.</summary>
        public void Stop()
        {
            lock (_syncRoot)
            {
                if (_engine == null) return;

                Trace.WriteLineIf(SimpleHttpServer.TrcLvl.TraceInfo, "SimpleHttpServer: stopping (engine=" + ActiveEngine + ")...");
                try { _engine.Stop(); }
                finally
                {
                    _engine.Dispose();
                    _engine = null;
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private IHttpEngine CreateEngine(HttpServerEngineKind pKind)
        {
            switch (pKind)
            {
                case HttpServerEngineKind.Socket:
                    return new SocketHttpEngine(Settings, DispatchRequest);
                case HttpServerEngineKind.HttpListener:
                    return new HttpListenerEngine(Settings, DispatchRequest);
                default:
                    throw new ArgumentOutOfRangeException(nameof(pKind), pKind, "Unsupported engine kind");
            }
        }

        /// <summary>Request processing pipeline shared by both engines (Simple.HttpServer.specs.md §6).</summary>
        private void DispatchRequest(IHttpContext pCtx)
        {
            try
            {
                if (!AllowedMethods.Contains(pCtx.HttpMethod, StringComparer.OrdinalIgnoreCase))
                {
                    pCtx.WriteErrorResponse(405, "Method Not Allowed");
                    return;
                }

                if (!pCtx.Headers.ContainsKey("Host"))
                {
                    pCtx.WriteErrorResponse(400, "Bad Request: missing Host header");
                    return;
                }

                ResolveActualUrl(pCtx);

                HandleServiceRequestMethod handler = FindAndStripHandler(pCtx);
                bool handled = handler != null && handler(pCtx);

                if (!handled)
                    ServeStaticFile(pCtx);

                if (!pCtx.WasSent) pCtx.Flush();
            }
            catch (Exception ex)
            {
                Trace.WriteLineIf(SimpleHttpServer.TrcLvl.TraceError, "SimpleHttpServer: unhandled exception processing request: " + ex);
                if (!pCtx.WasSent)
                {
                    try { pCtx.WriteErrorResponse(500, "Internal Server Error"); } catch (Exception) { }
                }
            }
        }

        private void ResolveActualUrl(IHttpContext pCtx)
        {
            if ((string.IsNullOrEmpty(pCtx.ActualUrl) || pCtx.ActualUrl == "/") && !string.IsNullOrEmpty(Settings.DefaultPage))
                pCtx.ActualUrl = Settings.DefaultPage;
        }

        private HandleServiceRequestMethod FindAndStripHandler(IHttpContext pCtx)
        {
            string bestPrefix = null;
            HandleServiceRequestMethod bestHandler = null;

            lock (_syncRoot)
            {
                foreach (var kv in _handlers)
                {
                    if (pCtx.ActualUrl.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase) &&
                        (bestPrefix == null || kv.Key.Length > bestPrefix.Length))
                    {
                        bestPrefix = kv.Key;
                        bestHandler = kv.Value;
                    }
                }
            }

            if (bestHandler != null)
            {
                pCtx.ActualUrl = pCtx.ActualUrl.Substring(bestPrefix.Length);
                if (string.IsNullOrEmpty(pCtx.ActualUrl)) pCtx.ActualUrl = "/";
                return bestHandler;
            }

            return DefaultServiceRequestHandler;
        }

        /// <summary>Resolves the request to a file under <see cref="SimpleHttpServerSettings.Directory"/>, preventing path traversal.</summary>
        private void ServeStaticFile(IHttpContext pCtx)
        {
            string root;
            try
            {
                root = Path.GetFullPath(Settings.Directory ?? ".");
            }
            catch (Exception)
            {
                pCtx.WriteErrorResponse(500, "Internal Server Error");
                return;
            }

            string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;

            string relative = pCtx.ActualUrl.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);

            string combined;
            try
            {
                combined = Path.GetFullPath(Path.Combine(root, relative));
            }
            catch (Exception)
            {
                pCtx.WriteErrorResponse(404, "Not Found");
                return;
            }

            if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                pCtx.WriteErrorResponse(404, "Not Found");
                return;
            }

            pCtx.WriteFileResponse(combined);
        }
    }
}
