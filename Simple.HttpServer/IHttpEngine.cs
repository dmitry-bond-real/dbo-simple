using System;

namespace Simple.HttpServer
{
    /// <summary>Internal transport-level contract shared by the socket and <see cref="System.Net.HttpListener"/> engines.</summary>
    internal interface IHttpEngine : IDisposable
    {
        /// <summary>True while the engine is accepting/processing connections.</summary>
        bool IsRunning { get; }

        /// <summary>Starts accepting connections on the configured port.</summary>
        void Start();

        /// <summary>Stops accepting new connections and waits (bounded) for in-flight requests to complete.</summary>
        void Stop();
    }
}
