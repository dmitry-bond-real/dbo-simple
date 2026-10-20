using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Simple.HttpServer
{
    /// <summary>Wraps a raw, already-parsed HTTP request from the <see cref="SocketHttpEngine"/>.</summary>
    internal sealed class SocketHttpContext : HttpContextBase
    {
        private readonly Stream _stream;
        private readonly MemoryStream _inputStream;

        public SocketHttpContext(SimpleHttpServerSettings pSettings, string pHttpMethod, string pUrl,
            Dictionary<string, string> pHeaders, byte[] pBody, Stream pStream, bool pIsLocal = false)
            : base(pSettings, pHttpMethod, pUrl, pHeaders, pIsLocal)
        {
            _stream = pStream;
            _inputStream = new MemoryStream(pBody ?? Array.Empty<byte>(), false);
        }

        public override Stream InputStream => _inputStream;

        /// <summary>True if the response explicitly requested the connection be closed (checked by the engine's keep-alive loop).</summary>
        public bool ConnectionCloseRequested { get; private set; }

        protected override void SendResponseCore(int pStatusCode, string pReasonPhrase, Dictionary<string, string> pHeaders, byte[] pContent, int pContentLength)
        {
            if (pContentLength <= 0) pContentLength = pContent.Length;

            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(pStatusCode).Append(' ').Append(pReasonPhrase).Append("\r\n");
            foreach (var kv in pHeaders)
            {
                sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
                if (string.Equals(kv.Key, "Connection", StringComparison.OrdinalIgnoreCase) &&
                    kv.Value.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ConnectionCloseRequested = true;
                }
            }
            sb.Append("\r\n");

            byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            _stream.Write(headerBytes, 0, headerBytes.Length);
            if (pContentLength > 0)
                _stream.Write(pContent, 0, pContentLength);
            _stream.Flush();
        }

        public override void Flush()
        {
            try { _stream.Flush(); } catch (Exception) { }
        }
    }
}
