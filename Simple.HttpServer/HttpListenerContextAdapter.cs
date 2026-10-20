using System;
using System.Collections.Generic;
using System.IO;
using System.Net;

namespace Simple.HttpServer
{
    /// <summary>Wraps a <see cref="HttpListenerContext"/> produced by the <see cref="HttpListenerEngine"/>.</summary>
    internal sealed class HttpListenerContextAdapter : HttpContextBase
    {
        private readonly HttpListenerContext _context;

        public HttpListenerContextAdapter(HttpListenerContext pContext, SimpleHttpServerSettings pSettings)
            : base(pSettings, pContext.Request.HttpMethod, pContext.Request.RawUrl, BuildHeaders(pContext.Request), pContext.Request.IsLocal)
        {
            _context = pContext;
        }

        public override Stream InputStream => _context.Request.InputStream;

        private static Dictionary<string, string> BuildHeaders(HttpListenerRequest pRequest)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in pRequest.Headers.AllKeys)
            {
                if (key == null) continue;
                headers[key] = pRequest.Headers[key];
            }
            return headers;
        }

        protected override void SendResponseCore(int pStatusCode, string pReasonPhrase, Dictionary<string, string> pHeaders, byte[] pContent, int pContentLength)
        {
            if (pContentLength <= 0) pContentLength = pContent.Length;

            HttpListenerResponse response = _context.Response;
            response.StatusCode = pStatusCode;
            response.StatusDescription = pReasonPhrase;

            foreach (var kv in pHeaders)
            {
                if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    response.ContentType = kv.Value;
                else if (string.Equals(kv.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    response.ContentLength64 = (long)pContentLength;
                else if (string.Equals(kv.Key, "Date", StringComparison.OrdinalIgnoreCase))
                    continue; // HttpListener emits its own Date header
                else
                    response.Headers[kv.Key] = kv.Value;
            }

            response.OutputStream.Write(pContent, 0, pContentLength);
        }

        public override void Flush()
        {
            try { _context.Response.OutputStream.Flush(); } catch (Exception) { }
            try { _context.Response.Close(); } catch (Exception) { }
        }
    }
}
