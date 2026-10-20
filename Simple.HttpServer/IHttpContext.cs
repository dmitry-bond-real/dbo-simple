/*
 * refs
 *   - https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistenerrequest.islocal?view=net-10.0
 */

using System.Collections.Generic;
using System.IO;

namespace Simple.HttpServer
{
    /// <summary>
    /// Abstracts an in-flight HTTP request/response exchange for service request handlers, regardless of
    /// which engine (<see cref="HttpServerEngineKind.Socket"/> or <see cref="HttpServerEngineKind.HttpListener"/>)
    /// produced it.
    /// </summary>
    public interface IHttpContext
    {
        /// <summary>Running counter uniquely identifying this request/response exchange (increments for every context created).</summary>
        long ID { get; }

        /// <summary>HTTP method of the request, e.g. "GET", "POST".</summary>
        string HttpMethod { get; }

        /// <summary>Full request path and query string, e.g. "/srvc/status?x=1".</summary>
        string Url { get; }

        /// <summary>Request path with the matched handler's URL-base prefix stripped; mutable so handlers can rewrite it.</summary>
        string ActualUrl { get; set; }

        /// <summary>Request headers, keyed case-insensitively.</summary>
        Dictionary<string, string> Headers { get; }

        /// <summary>Query-string parameters, URL-decoded.</summary>
        Dictionary<string, string> QueryParameters { get; }

        /// <summary>Form parameters parsed from an <c>application/x-www-form-urlencoded</c> request body.</summary>
        Dictionary<string, string> FormParameters { get; }

        /// <summary>Cookies parsed from the <c>Cookie</c> request header.</summary>
        Dictionary<string, string> Cookies { get; }

        /// <summary>Raw request body, read and decoded as text on first access.</summary>
        string Body { get; }

        /// <summary>Raw request body as a binary stream, read on first access.</summary>
        Stream InputStream { get; }

        /// <summary>True once a response has been written for this request.</summary>
        bool WasSent { get; }

        /// <summary>True when the request was sent from the local computer (mirrors <see cref="System.Net.HttpListenerRequest.IsLocal"/>).</summary>
        bool IsLocal { get; }

        /// <summary>Writes a complete response with the given status code, content type and body.</summary>
        void WriteResponse(int pStatusCode, string pContentType, byte[] pContent, int pContentLength = -1, Dictionary<string, string> pExtraHeaders = null);

        /// <summary>Serves a file from disk: 200 with the correct MIME type, or 404 if it does not exist.</summary>
        void WriteFileResponse(string pFilePath);

        /// <summary>Writes a minimal error response body (never includes stack traces or internal details).</summary>
        void WriteErrorResponse(int pStatusCode, string pMessage);

        /// <summary>Writes a 302 redirect response to the given URL.</summary>
        void Redirect(string pUrl);

        /// <summary>Flushes/completes the response to the underlying transport.</summary>
        void Flush();
    }
}
