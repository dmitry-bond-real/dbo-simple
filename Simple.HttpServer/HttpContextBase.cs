using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Simple.HttpServer
{
    /// <summary>
    /// Shared <see cref="IHttpContext"/> logic (query/form/cookie parsing, response composition, header
    /// sanitization) common to both engines. Engines only need to supply raw method/url/headers/body and the
    /// transport-specific code to actually send bytes back to the client.
    /// </summary>
    internal abstract class HttpContextBase : IHttpContext
    {
        private static readonly Dictionary<int, string> FallbackReasons = new Dictionary<int, string>
        {
            [200] = "OK",
            [301] = "Moved Permanently",
            [302] = "Found",
            [400] = "Bad Request",
            [401] = "Unauthorized",
            [403] = "Forbidden",
            [404] = "Not Found",
            [405] = "Method Not Allowed",
            [500] = "Internal Server Error",
            [503] = "Service Unavailable"
        };

        private static long _globalId;

        private readonly SimpleHttpServerSettings _settings;
        private readonly Lazy<Dictionary<string, string>> _queryParameters;
        private readonly Lazy<Dictionary<string, string>> _formParameters;
        private readonly Lazy<Dictionary<string, string>> _cookies;
        private readonly Lazy<string> _body;

        protected HttpContextBase(SimpleHttpServerSettings pSettings, string pHttpMethod, string pUrl, Dictionary<string, string> pHeaders, bool pIsLocal = false)
        {
            _settings = pSettings;
            ID = Interlocked.Increment(ref _globalId);
            HttpMethod = pHttpMethod ?? string.Empty;
            Url = pUrl ?? string.Empty;
            ActualUrl = GetPathPart(Url);
            Headers = pHeaders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IsLocal = pIsLocal;

            _queryParameters = new Lazy<Dictionary<string, string>>(() => ParseUrlEncoded(GetQueryPart(Url)));
            _cookies = new Lazy<Dictionary<string, string>>(ParseCookies);
            _formParameters = new Lazy<Dictionary<string, string>>(ParseForm);
            _body = new Lazy<string>(ReadBodyAsText);
        }

        public long ID { get; }
        public string HttpMethod { get; }
        public string Url { get; }
        public string ActualUrl { get; set; }
        public Dictionary<string, string> Headers { get; }
        public Dictionary<string, string> QueryParameters => _queryParameters.Value;
        public Dictionary<string, string> FormParameters => _formParameters.Value;
        public Dictionary<string, string> Cookies => _cookies.Value;
        public string Body => _body.Value;
        public abstract Stream InputStream { get; }
        public bool WasSent { get; private set; }
        public bool IsLocal { get; }

        public void WriteResponse(int pStatusCode, string pContentType, byte[] pContent, int pContentLength = -1, Dictionary<string, string> pExtraHeaders = null)
        {
            if (WasSent) return;
            byte[] content = pContent ?? Array.Empty<byte>();
            if (pContentLength <= 0) pContentLength = content.Length;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = string.IsNullOrEmpty(pContentType) ? "application/octet-stream" : pContentType,
                ["Content-Length"] = pContentLength.ToString(),
                ["Date"] = DateTime.UtcNow.ToString("r")
            };

            if (_settings?.ExtraHttpReplyHeaders != null)
            {
                foreach (var kv in _settings.ExtraHttpReplyHeaders)
                    headers[kv.Key] = SanitizeHeaderValue(kv.Value);
            }

            if (pExtraHeaders != null)
            {
                foreach (var kv in pExtraHeaders)
                    headers[kv.Key] = SanitizeHeaderValue(kv.Value);
            }

            WasSent = true;
            SendResponseCore(pStatusCode, ReasonPhraseFor(pStatusCode), headers, content, pContentLength);
        }

        public void WriteFileResponse(string pFilePath)
        {
            if (WasSent) return;
            if (string.IsNullOrEmpty(pFilePath) || !File.Exists(pFilePath))
            {
                WriteErrorResponse(404, "Not Found");
                return;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(pFilePath);
            }
            catch (UnauthorizedAccessException)
            {
                WriteErrorResponse(403, "Forbidden");
                return;
            }
            catch (IOException)
            {
                WriteErrorResponse(500, "Internal Server Error");
                return;
            }

            string ext = Path.GetExtension(pFilePath).TrimStart('.');
            string contentType = "application/octet-stream";
            if (_settings?.MimeTypes != null && _settings.MimeTypes.TryGetValue(ext, out string mapped))
                contentType = mapped;

            WriteResponse(200, contentType, bytes);
        }

        public void WriteErrorResponse(int pStatusCode, string pMessage)
        {
            string reason = ReasonPhraseFor(pStatusCode);
            string safeMessage = SanitizeHeaderValue(pMessage ?? reason);
            string body = "<html><body><h1>" + pStatusCode + " " + reason + "</h1><p>"
                + System.Net.WebUtility.HtmlEncode(safeMessage) + "</p></body></html>";
            WriteResponse(pStatusCode, "text/html", Encoding.UTF8.GetBytes(body));
        }

        public void Redirect(string pUrl)
        {
            if (WasSent) return;
            var headers = new Dictionary<string, string> { ["Location"] = SanitizeHeaderValue(pUrl) };
            WriteResponse(302, "text/plain", Array.Empty<byte>(), -1, headers);
        }

        public abstract void Flush();

        protected abstract void SendResponseCore(int pStatusCode, string pReasonPhrase, Dictionary<string, string> pHeaders, byte[] pContent, int pContentLength);

        private string ReasonPhraseFor(int pStatusCode)
        {
            string key = pStatusCode.ToString();
            if (_settings?.HttpReplyCodes != null && _settings.HttpReplyCodes.TryGetValue(key, out string phrase))
                return phrase;
            return FallbackReasons.TryGetValue(pStatusCode, out string fallback) ? fallback : "Unknown";
        }

        private string ReadBodyAsText()
        {
            Stream stream = InputStream;
            if (stream == null) return string.Empty;

            Encoding encoding = Encoding.UTF8;
            if (Headers.TryGetValue("Content-Type", out string contentType))
            {
                int idx = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string charset = contentType.Substring(idx + "charset=".Length).Trim(' ', '"', ';');
                    try { encoding = Encoding.GetEncoding(charset); } catch (ArgumentException) { }
                }
            }

            using (var reader = new StreamReader(stream, encoding, false, 1024, true))
                return reader.ReadToEnd();
        }

        private static string GetPathPart(string pUrl)
        {
            int q = pUrl.IndexOf('?');
            return q >= 0 ? pUrl.Substring(0, q) : pUrl;
        }

        private static string GetQueryPart(string pUrl)
        {
            int q = pUrl.IndexOf('?');
            return q >= 0 ? pUrl.Substring(q + 1) : string.Empty;
        }

        private static Dictionary<string, string> ParseUrlEncoded(string pData)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(pData)) return result;

            foreach (string pair in pData.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string rawKey = eq >= 0 ? pair.Substring(0, eq) : pair;
                string rawValue = eq >= 0 ? pair.Substring(eq + 1) : string.Empty;
                string key = Uri.UnescapeDataString(rawKey.Replace("+", " "));
                if (key.Length == 0) continue;
                result[key] = Uri.UnescapeDataString(rawValue.Replace("+", " "));
            }
            return result;
        }

        private Dictionary<string, string> ParseCookies()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Headers.TryGetValue("Cookie", out string cookieHeader) || string.IsNullOrEmpty(cookieHeader))
                return result;

            foreach (string part in cookieHeader.Split(';'))
            {
                string[] kv = part.Split(new[] { '=' }, 2);
                string name = kv[0].Trim();
                if (name.Length == 0) continue;
                string value = kv.Length > 1 ? kv[1].Trim() : string.Empty;
                try { value = Uri.UnescapeDataString(value); } catch (UriFormatException) { }
                result[name] = value;
            }
            return result;
        }

        private Dictionary<string, string> ParseForm()
        {
            if (Headers.TryGetValue("Content-Type", out string contentType) &&
                contentType.IndexOf("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ParseUrlEncoded(Body);
            }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Strips CR/LF from a value before it is placed in a response header, to prevent header/response splitting.</summary>
        internal static string SanitizeHeaderValue(string pValue)
        {
            if (string.IsNullOrEmpty(pValue)) return pValue ?? string.Empty;
            return pValue.Replace("\r", string.Empty).Replace("\n", string.Empty);
        }
    }
}
