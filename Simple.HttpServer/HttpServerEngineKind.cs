namespace Simple.HttpServer
{
    /// <summary>Selects which transport engine <see cref="SimpleHttpServer"/> uses to accept and process HTTP requests.</summary>
    public enum HttpServerEngineKind
    {
        /// <summary>Resolve to a concrete engine automatically. Currently resolves to <see cref="HttpListener"/>.</summary>
        Auto,

        /// <summary>Raw <see cref="System.Net.Sockets.TcpListener"/>-based engine with hand-parsed HTTP/1.1.</summary>
        Socket,

        /// <summary><see cref="System.Net.HttpListener"/> (HTTP.SYS)-based engine.</summary>
        HttpListener
    }
}
