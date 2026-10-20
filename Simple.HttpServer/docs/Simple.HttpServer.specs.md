
# Intro

We need a *.NET Framework 4.x* lib which will provide functionality of simple *HTTP Server*.

# Specification: `Simple.HttpServer` component (.NET Framework 4.8)

## 1. Purpose

Build a new, clean implementation of a lightweight embeddable HTTP server component,
targeting **.NET Framework 4.8**, that:

1. Supports the basic feature set of **HTTP/1.1** (see §5).
2. Is **API-compatible** (drop-in replaceable) with the existing in-house HTTP server
   component found in `old-lib/` (`Simple.HttpServer.cs` / `Standard.HttpServer.cs`),
   so existing application code that uses that component can switch to the new one
   with little or no changes.
3. Ships as **two interchangeable engine implementations** behind the same public API
   (see §2.1): a raw-socket engine and a `System.Net.HttpListener`-based engine, so that
   deployments affected by HTTP.SYS driver limitations can fall back to the socket engine
   without changing any calling code.

The new component must be self-contained (BCL only, no dependency on `XService.Net2`,
`Newtonsoft.Json`, or any other third-party/in-house package used by `old-lib/`) unless
explicitly decided otherwise.

## 2. Reference material

The `old-lib/` sub-folder contains the previous implementation(s) for reference only
(it is **not** to be copied/reused as-is):

| File | Role |
|---|---|
| `Simple.HttpServer.cs` | `SimpleHttpServer` — raw `TcpListener`/socket based HTTP engine, hand-parses the HTTP request. |
| `Standard.HttpServer.cs` | `StandardHttpServer` — `System.Net.HttpListener`-based HTTP engine; owns `ConfigSectionName`, sessions, login/auth. |
| `Simple.HttpServerSettings.cs` | `SimpleHttpServerSettings` — settings holder, loadable from `app.config` as a custom config section. |
| `Simple.Adapters.cs` | `HttpIOAdapter` — unifies access to request/response regardless of which engine produced it. |
| `Simple.Constants.cs` | Config attribute names, default MIME-type map, default reply headers. |
| `PwdEncryptor.cs` | Password hashing helper used by the login feature (out of scope, see §10). |

Use these files to understand **naming and shape of the public API** that must be
preserved. The new implementation must **keep both engines** found in `old-lib/`
(socket-based and `HttpListener`-based) rather than standardizing on one, for the reason
explained in §2.1. It must still **drop** the login/session/authentication subsystem
(see §4 — Non-Goals).

## 2.1 Why two implementations

`System.Net.HttpListener` is a thin wrapper around the Windows **HTTP.SYS** kernel-mode
driver. On some target systems this causes operational problems that a pure managed
socket implementation does not have, for example:

- **URL reservation / access-denied errors**: binding a non-wildcard, non-localhost
  prefix (e.g. `http://+:8080/`) requires an admin-elevated `netsh http add urlacl`
  reservation beforehand, or the process must run elevated; this is often impractical
  for a service account used in restricted/locked-down environments.
- **Port ownership conflicts**: HTTP.SYS arbitrates port 80/443 (and any registered
  prefix) system-wide; IIS, other HTTP.SYS-based services, or a previous crashed
  instance can hold a conflicting registration, causing `HttpListener.Start()` to fail
  with `AddressAccessDenied` or `AddressAlreadyInUse` even though no other **socket**
  is actually listening.
- **Driver availability**: HTTP.SYS can be disabled, unavailable, or behave differently
  on certain Windows SKUs/Server Core/locked-down environments, or inside some
  containers — breaking `HttpListener` entirely in those environments.
- **Diagnosability**: HTTP.SYS-level failures surface as opaque Win32 error codes that
  are harder to troubleshoot in the field than a plain `SocketException` from a
  directly-owned `TcpListener`.

For these reasons, this component must ship **two engine implementations** behind the
same public `SimpleHttpServer` API, so that an application experiencing HTTP.SYS-related
problems in a given deployment can switch engines via configuration (§2.2) without any
code changes:

| Engine | Based on | Old-lib precedent | When to prefer it |
|---|---|---|---|
| `Socket` | Raw `TcpListener`/`Socket`, hand-parsed HTTP | `Simple.HttpServer.cs` (`SimpleHttpServer`) | HTTP.SYS unavailable/restricted; need to run unprivileged on an arbitrary/non-reserved port; simplest deployment footprint. |
| `HttpListener` | `System.Net.HttpListener` | `Standard.HttpServer.cs` (`StandardHttpServer`) | Normal/default case — gets HTTP/1.1 protocol correctness (keep-alive, chunked requests, etc.) for free from the OS. |

Both engines must pass the **same** test suite (§11, §13) and must produce equivalent
observable behavior (status codes, headers, routing) for everything in scope (§5); they
differ only in transport-level plumbing and in the handful of OS-driver-related failure
modes described above.

## 2.2 Engine selection

Engine choice is a `Settings`-level concern, not a different class to instantiate:

- `SimpleHttpServerSettings.Engine` (new enum property, see §4.2) selects the engine:
  `HttpServerEngineKind.Auto` (default — currently resolves to `HttpListener`),
  `HttpServerEngineKind.Socket`, or `HttpServerEngineKind.HttpListener`.
- `SimpleHttpServer` is a thin façade: at `Start()` time it constructs the selected
  internal engine (`SocketHttpEngine` or `HttpListenerHttpEngine`, both implementing a
  shared **internal** `IHttpEngine` interface) and delegates `Stop()`/`IsRunning`/request
  dispatch to it. `AddServiceRequestHandler`/`RemoveServiceRequestHandler`/`Settings`/
  `ConfigSectionName` remain on the façade and are engine-agnostic.
- Switching `Settings.Engine` while the server `IsRunning` only takes effect on the next
  `Start()` after a `Stop()` — it must not require recompiling or re-registering handlers.
- If `HttpServerEngineKind.HttpListener` is requested but construction fails (e.g.
  `HttpListenerException` from HTTP.SYS), log a `Warning` trace and **fail fast** by
  default (do not silently fall back) — forcing an explicit, visible configuration
  decision; an opt-in `AutoFallbackToSocketEngine` flag may allow automatic fallback for
  environments that want best-effort behavior.

## 3. Goals

- Two engines (`Socket`, `HttpListener`) selectable via `Settings.Engine`, both
  implementing the identical public API and producing equivalent observable behavior
  (§2.1, §2.2) — this is what lets deployments route around HTTP.SYS problems.
- Correct, standards-reasonable HTTP/1.1 request/response handling (§5).
- Static file serving from a configured root directory, with MIME-type mapping.
- Pluggable "service" request handlers, dispatched by URL-prefix (`AddServiceRequestHandler`).
- Configuration via a custom `app.config` section, or programmatically via a `Settings` object.
- Thread-safe: can accept multiple concurrent connections; handler registration can be
  mutated at runtime while the server is running.
- Predictable lifecycle: `Start()` / `Stop()` are idempotent and safe to call repeatedly.
- Observable via `System.Diagnostics.Trace` (a `TraceSwitch`), matching the logging
  style of the old component so that existing trace configuration keeps working.

## 4. Non-Goals (explicitly out of scope)

- HTTP/2, HTTP/3, WebSockets.
- Built-in authentication, login pages, or session/cookie-based login (`LoginRequired`,
  `CheckIfLoggedIn`, master-key, `PwdEncryptor`, etc. from `old-lib/`). The new component
  may still expose **request-level session/cookie read access** (see `IHttpContext.Cookies`
  in §4.3), but it must not implement its own login subsystem.
- TLS/SSL termination (HTTPS can be added later by registering an `https://` prefix with
  `HttpListener` and binding a certificate via `netsh`/SNI outside of this component).
- Full RFC 7230 edge-case compliance (e.g. obscure transfer-encodings, HTTP pipelining
  corner cases). "Basic HTTP/1.1 features" per §5 is sufficient.
- Unifying the two engines into a single implementation (deleting either one) is out of
  scope — both must be kept and maintained side by side (§2.1).

## 4.1 Public API contract — `SimpleHttpServer` class

The main class must be named `SimpleHttpServer` (namespace `Simple.HttpServer`) and expose
at least the following public surface (mirroring `StandardHttpServer` from `old-lib/`,
since that is the variant that already has `ConfigSectionName`):

```csharp
namespace Simple.HttpServer
{
    public enum HttpServerEngineKind { Auto, Socket, HttpListener }

    public class SimpleHttpServer : IDisposable
    {
        // --- construction ---
        public SimpleHttpServer();
        public SimpleHttpServer(string pConfigSectionName);
        public SimpleHttpServer(SimpleHttpServerSettings pSettings);
        public SimpleHttpServer(SimpleHttpServerSettings pSettings, HttpServerEngineKind pEngine); // explicit override

        // --- properties ---
        public string ConfigSectionName { get; }
        public SimpleHttpServerSettings Settings { get; }
        public bool IsRunning { get; }
        public HttpServerEngineKind ActiveEngine { get; }   // engine actually started (Auto gets resolved)
        public List<string> AllowedMethods { get; }     // e.g. GET, POST, PUT, DELETE, HEAD, OPTIONS
        public object SyncRoot { get; }

        // --- request handler plumbing ---
        public delegate bool HandleServiceRequestMethod(IHttpContext pCtx);
        public HandleServiceRequestMethod DefaultServiceRequestHandler { get; set; }

        public void AddServiceRequestHandler(string pUrlBase, HandleServiceRequestMethod pHandler);
        public void RemoveServiceRequestHandler(HandleServiceRequestMethod pHandler);

        // --- lifecycle ---
        public void Start();
        public void Stop();
        public void Dispose();
    }
}
```

Notes:
- `AddServiceRequestHandler`/`RemoveServiceRequestHandler` behavior must match the old
  component: handlers are matched by the **longest matching URL-prefix** (case-insensitive);
  registering the same prefix twice replaces the existing handler.
- `Start()` must be a no-op if already running or if `Settings.Enabled == false`.
- `Stop()` must be a no-op if not running, and must be safe to call from `Dispose()`.
- Constructors must not throw if no `app.config` section is present — fall back to
  `SimpleHttpServerSettings.CreateDefault()`.
- `Start()` resolves `HttpServerEngineKind.Auto` to a concrete engine (currently
  `HttpListener`), constructs the matching internal engine, and exposes the result via
  `ActiveEngine`; the explicit constructor overload / `Settings.Engine` bypasses
  auto-resolution.
- Both engines must honor identical `AddServiceRequestHandler`/`RemoveServiceRequestHandler`
  semantics — the façade owns the handler list, not the engine, so handlers survive an
  engine switch across `Stop()`/`Start()`.

## 4.2 Public API contract — `SimpleHttpServerSettings` class

```csharp
namespace Simple.HttpServer
{
    public class SimpleHttpServerSettings : IConfigurationSectionHandler
    {
        public static SimpleHttpServerSettings CreateDefault();
        public static SimpleHttpServerSettings LoadFromConfig(string pConfigSectionName);

        public bool Enabled { get; set; }
        public HttpServerEngineKind Engine { get; set; } // Auto (default) | Socket | HttpListener — see §2.1, §2.2
        public int Port { get; set; }
        public string Directory { get; set; }            // document root for static files
        public int ConnectQueueLength { get; set; }
        public string ServiceUrlPrefix { get; set; }      // e.g. "/srvc" — requests under this prefix bypass static-file lookup
        public string DefaultPage { get; set; }           // served when URL is "/" or empty
        public int SessionExpireTimeMin { get; set; }      // kept only if request-session tracking (§4.3) is implemented

        public Dictionary<string, string> MimeTypes { get; }           // extension -> content-type, seeded with sane defaults
        public Dictionary<string, string> HttpReplyCodes { get; }      // numeric code -> reason phrase, seeded with sane defaults
        public Dictionary<string, string> ExtraHttpReplyHeaders { get; }

        // IConfigurationSectionHandler
        public object Create(object parent, object configContext, XmlNode section);
    }
}
```

Config section example (`app.config`):

```xml
<configuration>
  <configSections>
    <section name="SimpleHttpServer" type="Simple.HttpServer.SimpleHttpServerSettings, Simple.HttpServer" />
  </configSections>

  <SimpleHttpServer enabled="true" engine="Auto" port="8080" directory=".\httproot\" connectQueueLength="200"
                     serviceUrlPrefix="/srvc" defaultPage="/index.html">
    <MimeTypes>
      XSL = text/xml
    </MimeTypes>
  </SimpleHttpServer>
</configuration>
```

## 4.3 Public API contract — request context (`IHttpContext` / `HttpIOAdapter`)

Service request handlers receive a single context object that abstracts over
`System.Net.HttpListenerContext`. Minimum surface:

```csharp
public interface IHttpContext
{
    string HttpMethod { get; }     // "GET", "POST", ...
    string Url { get; }            // full path + query, e.g. "/srvc/status?x=1"
    string ActualUrl { get; set; } // path with matched handler's UrlBase prefix stripped
    Dictionary<string, string> Headers { get; }
    Dictionary<string, string> QueryParameters { get; }   // parsed from query string
    Dictionary<string, string> FormParameters { get; }    // parsed from application/x-www-form-urlencoded body
    Dictionary<string, string> Cookies { get; }
    string Body { get; }           // raw request body as text (lazy-read)
    Stream InputStream { get; }    // raw request body as binary (lazy-read)

    bool WasSent { get; }
    void WriteResponse(int pStatusCode, string pContentType, byte[] pContent, Dictionary<string, string> pExtraHeaders = null);
    void WriteFileResponse(string pFilePath);              // 200 + correct MIME type, or 404 if missing
    void WriteErrorResponse(int pStatusCode, string pMessage);
    void Redirect(string pUrl);
    void Flush();
}
```

This mirrors the role of `HttpIOAdapter` in `old-lib/`, but trimmed down to only what a
"simple" server needs (no login/session fields).

## 5. HTTP/1.1 functional requirements

These requirements apply to **both** the `Socket` and `HttpListener` engines (§2.1)
unless an item explicitly calls out a per-engine difference. The server must correctly
implement:

1. **Request line parsing**: `METHOD SP request-target SP HTTP-Version CRLF`.
2. **Methods**: `GET`, `HEAD`, `POST`, `PUT`, `DELETE`, `OPTIONS` at minimum (reported via
   `AllowedMethods`); unsupported methods → `405 Method Not Allowed`.
3. **Headers**: case-insensitive header names; multi-value `Set-Cookie` supported; request
   must have a `Host` header (per RFC 7230) — missing `Host` → `400 Bad Request`.
4. **Persistent connections (keep-alive)**: default for HTTP/1.1 unless `Connection: close`
   is sent by the client or by the server.
   - `HttpListener` engine: rely on its built-in handling; do not re-implement
     socket-level keep-alive on top of it.
   - `Socket` engine: must implement keep-alive itself (reuse the accepted socket for
     subsequent requests until `Connection: close`, an idle timeout, or a safety
     max-requests-per-connection limit is reached).
5. **Content-Length**: responses must always include an accurate `Content-Length` (chunked
   transfer-encoding for responses is a nice-to-have, not required).
6. **Status line & reason phrase**: `HTTP/1.1 <code> <reason>`(via `HttpReplyCodes` map,
   with standard defaults: 200 OK, 301/302, 400, 401, 403, 404, 405, 500, 503).
7. **Date header**: every response includes an RFC 1123 `Date` header (UTC).
8. **Query string parsing**: `?a=1&b=2` decoded into `QueryParameters` (URL-decoded).
9. **Form body parsing**: `Content-Type: application/x-www-form-urlencoded` POST bodies
   decoded into `FormParameters`.
10. **Static file serving**: requests not matching any registered service-handler prefix
    are resolved as files under `Settings.Directory`; correct `Content-Type` from
    `Settings.MimeTypes` by file extension; `404` if the file does not exist; must prevent
    path traversal outside `Settings.Directory` (reject `..` segments / resolve full path
    and verify it is still under the root — see §9 Security).
11. **Cookies**: parse `Cookie` request header into `Cookies`; allow handlers to add
    `Set-Cookie` response headers via `pExtraHeaders`.
12. **Graceful shutdown**: `Stop()` must stop accepting new connections and let
    in-flight requests complete (bounded wait), not just kill sockets.

## 6. Request processing pipeline

For every incoming request:

1. Resolve `ActualUrl`; if empty or `"/"`, substitute `Settings.DefaultPage` (if set).
2. If `ActualUrl` starts with `Settings.ServiceUrlPrefix` (or no service prefix is
   configured — treat all URLs as eligible): look up the best-matching (longest-prefix)
   registered handler via `AddServiceRequestHandler`; if found, invoke it, then `Flush()`
   the response if the handler did not already send one.
3. If handler not found, attempt static-file resolution under `Settings.Directory`.
4. If neither matches, reply `404 Not Found`.
5. Any unhandled exception while processing the request → log it (Trace) and reply
   `500 Internal Server Error` with a minimal, non-sensitive error body (never leak stack
   traces to the client — see §9).

## 7. Threading & concurrency

- `HttpListener` engine: use `HttpListener.BeginGetContext`/`EndGetContext` (or
  `GetContextAsync` with `async`/`await`, both acceptable on net48) to accept requests
  without blocking a dedicated thread per connection; dispatch each request to the
  `ThreadPool` for handling.
- `Socket` engine: use `TcpListener.BeginAcceptTcpClient`/`EndAcceptTcpClient` (or the
  `async` equivalents) to accept connections; dispatch request parsing/handling for each
  accepted socket to the `ThreadPool`; apply a read timeout per connection so a
  slow/malicious client cannot hold a thread indefinitely.
- Both engines: all mutable shared state (`serviceRequestHandlers` list, running flag)
  lives on the `SimpleHttpServer` façade and must be guarded by locks
  (`lock (this.SyncRoot)` or a dedicated private sync object) — registering or removing
  handlers while the server is running must be safe and immediately effective regardless
  of which engine is active.
- `Start()`/`Stop()` themselves must be safe to call concurrently from multiple threads
  (e.g. guard with a lock + `IsRunning` check), for both engines.

## 8. Logging & diagnostics

- Expose a `static readonly TraceSwitch` (or reuse a `TraceSource`) named `SimpleHttpServer`
  so existing `app.config` `<system.diagnostics><switches>` configuration continues to work:
  ```xml
  <switches>
    <add name="SimpleHttpServer" value="4" />
  </switches>
  ```
- Trace at appropriate levels: `Verbose` for per-request detail, `Info` for start/stop and
  routing decisions, `Warning`/`Error` for recoverable/unrecoverable failures.
- Never write request/response bodies containing credentials or secrets to trace output.

## 9. Security considerations (OWASP-relevant)

- **Path traversal**: canonicalize the requested file path and verify it is still inside
  `Settings.Directory` before serving; reject otherwise with `403`/`404`.
- **Error disclosure**: `500` responses must not include stack traces or internal
  exception details in the body sent to the client (log them server-side via Trace only).
- **Header injection**: reject or sanitize request header values containing `\r`/`\n`
  before echoing them anywhere (e.g. redirects, logs used for display).
- **Resource exhaustion**: enforce a reasonable max request size / header count via
  `HttpListener` defaults; do not buffer unbounded request bodies in memory.
- **Host header**: validate presence (see §5.3); do not trust it for building absolute
  URLs used in security decisions without validation.
- **Engine-specific exposure**: the `Socket` engine hand-parses the HTTP request and is
  therefore directly responsible for enforcing request-line/header size limits and
  malformed-request rejection that `HttpListener`/HTTP.SYS normally enforces for free —
  do not treat the `Socket` engine as equivalently hardened without those explicit limits
  (max header count/size, max request-line length, read timeout — see §7).

## 10. Non-functional requirements

- Target framework: `net48` (matches `Simple.HttpServer.csproj`).
- No required third-party NuGet packages; BCL only (`System.Net`, `System.Threading`, etc.).
- Public types must have XML doc comments on all public members (consistent with existing
  project style).
- `Nullable` disabled, `ImplicitUsings` disabled — match current `.csproj` settings.
- No breaking of existing `AllowedMethods`, `IsRunning`, `ConfigSectionName`, `Settings`
  property names/types — these are the compatibility surface called out by the consumer.

## 11. Acceptance criteria / test plan

Provide unit/integration tests (e.g. using `HttpClient` against a `SimpleHttpServer`
started on an ephemeral port) covering:

1. `Start()`/`Stop()`/`Dispose()` lifecycle, including calling `Stop()` before `Start()`
   and calling `Start()` twice (idempotency).
2. `AddServiceRequestHandler("/srvc/ping", ...)` responds correctly to `GET /srvc/ping`,
   and longest-prefix-match wins when two overlapping prefixes are registered.
3. `RemoveServiceRequestHandler` stops routing to a previously-registered handler.
4. Static file serving: existing file → `200` + correct `Content-Type`; missing file →
   `404`; path-traversal attempt (`/../../secrets.txt`) → rejected.
5. `DefaultPage` substitution when requesting `/`.
6. Unknown method → `405`; missing `Host` header → `400`.
7. Concurrent requests: N parallel clients hitting the server complete successfully.
8. Config-section loading from `app.config` produces the same `Settings` as constructing
   `SimpleHttpServerSettings` programmatically with equivalent values.
9. Unhandled exception in a handler → client receives `500` with a generic body (no stack
   trace), and the error is traced.
10. The full suite above passes against **both** `Settings.Engine = Socket` and
    `Settings.Engine = HttpListener` (§2.1, §13.2), with identical observable results.

## 12. Example usage

```csharp
var server = new SimpleHttpServer(); // loads "SimpleHttpServer" section from app.config, or defaults

server.AddServiceRequestHandler("/srvc/status", ctx =>
{
    var json = "{\"status\":\"ok\"}";
    ctx.WriteResponse(200, "application/json", Encoding.UTF8.GetBytes(json));
    return true;
});

server.Start();
Console.WriteLine($"Listening on port {server.Settings.Port}, running={server.IsRunning}");

// ... later
server.Stop();
server.Dispose();
```

## 13. Suggested QA / test code

### 13.1 Test project setup

Create a separate `Simple.HttpServer.Tests` project (also `net48`), referencing:
- **NUnit** + **NUnit3TestAdapter** + **Microsoft.NET.Test.Sdk** (or MSTest/xUnit — any is
  fine, pick whatever is already standard for this repo's other test projects).
- `System.Net.Http` (BCL) for `HttpClient`-based integration tests.

These are **test-only** dependencies — they do not violate the "BCL only" rule for the
shipped `Simple.HttpServer` library itself (§10).

Tests should be split into two categories:
- **Unit tests**: pure logic with no socket involved — URL-prefix matching/longest-match
  selection, MIME-type lookup, settings parsing from an in-memory `XmlNode`/`app.config`,
  path-traversal canonicalization helper.
- **Integration tests**: start a real `SimpleHttpServer` on an ephemeral port and drive it
  with `HttpClient` (and, where `HttpClient` refuses to send malformed input, a raw
  `TcpClient` — see §13.4) end-to-end; every integration test must run against **both**
  `HttpServerEngineKind.Socket` and `HttpServerEngineKind.HttpListener` (see §13.2's
  `[TestFixture(...)]` parameterization) to satisfy acceptance criterion §11.10.

### 13.2 Test helper — ephemeral port + fixture

```csharp
internal static class TestPorts
{
    public static int GetFreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

[TestFixture(HttpServerEngineKind.Socket)]
[TestFixture(HttpServerEngineKind.HttpListener)]
public class SimpleHttpServerIntegrationTests
{
    private readonly HttpServerEngineKind engine;
    private string root;
    private SimpleHttpServer server;
    private HttpClient client;

    public SimpleHttpServerIntegrationTests(HttpServerEngineKind pEngine)
    {
        engine = pEngine;
    }

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "httptest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "hello.html"), "<html>hi</html>");

        var settings = SimpleHttpServerSettings.CreateDefault();
        settings.Engine = engine;
        settings.Port = TestPorts.GetFreeTcpPort();
        settings.Directory = root;

        server = new SimpleHttpServer(settings);
        server.Start();

        client = new HttpClient { BaseAddress = new Uri($"http://localhost:{settings.Port}/") };
    }

    [TearDown]
    public void TearDown()
    {
        client.Dispose();
        server.Stop();
        server.Dispose();
        Directory.Delete(root, true);
    }
}
```

NUnit's `[TestFixture(HttpServerEngineKind...)]` constructor parameterization runs every
`[Test]` method in §13.3 **twice** — once per engine — with no changes needed to the
individual test methods.

### 13.3 Scenario tests (mapped to §11 acceptance criteria)

```csharp
[Test]
public async Task StaticFile_Exists_Returns200_WithCorrectContentType()
{
    var resp = await client.GetAsync("hello.html");
    Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
    Assert.AreEqual("text/html", resp.Content.Headers.ContentType.MediaType);
}

[Test]
public async Task StaticFile_Missing_Returns404()
{
    var resp = await client.GetAsync("missing.html");
    Assert.AreEqual(HttpStatusCode.NotFound, resp.StatusCode);
}

[Test]
public async Task PathTraversal_IsRejected()
{
    var resp = await client.GetAsync("../../../windows/win.ini");
    Assert.That(resp.StatusCode,
        Is.EqualTo(HttpStatusCode.NotFound).Or.EqualTo(HttpStatusCode.Forbidden));
}

[Test]
public async Task ServiceHandler_LongestPrefixWins()
{
    server.AddServiceRequestHandler("/srvc", ctx =>
        { ctx.WriteResponse(200, "text/plain", Encoding.UTF8.GetBytes("short")); return true; });
    server.AddServiceRequestHandler("/srvc/ping", ctx =>
        { ctx.WriteResponse(200, "text/plain", Encoding.UTF8.GetBytes("pong")); return true; });

    var body = await client.GetStringAsync("srvc/ping");
    Assert.AreEqual("pong", body);
}

[Test]
public async Task RemoveServiceRequestHandler_StopsRouting()
{
    SimpleHttpServer.HandleServiceRequestMethod handler = ctx =>
        { ctx.WriteResponse(200, "text/plain", Encoding.UTF8.GetBytes("v1")); return true; };
    server.AddServiceRequestHandler("/srvc/x", handler);
    server.RemoveServiceRequestHandler(handler);

    var resp = await client.GetAsync("srvc/x");
    Assert.AreEqual(HttpStatusCode.NotFound, resp.StatusCode);
}

[Test]
public async Task DefaultPage_IsServed_ForRootUrl()
{
    server.Settings.DefaultPage = "/hello.html";
    var resp = await client.GetAsync("");
    Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
}

[Test]
public async Task UnknownMethod_Returns405()
{
    var req = new HttpRequestMessage(new HttpMethod("TRACE"), "hello.html");
    var resp = await client.SendAsync(req);
    Assert.AreEqual(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
}

[Test]
public async Task UnhandledHandlerException_Returns500_WithoutStackTrace()
{
    server.AddServiceRequestHandler("/srvc/boom", ctx => { throw new InvalidOperationException("boom"); });

    var resp = await client.GetAsync("srvc/boom");
    var body = await resp.Content.ReadAsStringAsync();

    Assert.AreEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
    StringAssert.DoesNotContain("InvalidOperationException", body);
    StringAssert.DoesNotContain("at Simple.HttpServer", body); // no stack frames leaked
}

[Test]
public async Task ConcurrentRequests_AllSucceed()
{
    var tasks = Enumerable.Range(0, 25).Select(_ => client.GetAsync("hello.html"));
    var results = await Task.WhenAll(tasks);
    Assert.IsTrue(results.All(r => r.StatusCode == HttpStatusCode.OK));
}

[Test]
public void StartStop_IsIdempotent()
{
    server.Start();              // already running -> no-op, must not throw
    Assert.IsTrue(server.IsRunning);

    server.Stop();
    server.Stop();                // already stopped -> no-op, must not throw
    Assert.IsFalse(server.IsRunning);
}
```

### 13.4 Wire-level tests (raw socket) for cases `HttpClient` can't produce

`HttpClient`/`HttpWebRequest` validate and normalize requests, so they cannot be used to
send a missing/malformed `Host` header or header-injection payloads. Use a raw `TcpClient`
helper for these:

```csharp
internal static class RawHttp
{
    public static async Task<string> SendAsync(int port, string rawRequest)
    {
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync("127.0.0.1", port);
            using (var stream = tcp.GetStream())
            {
                byte[] data = Encoding.ASCII.GetBytes(rawRequest);
                await stream.WriteAsync(data, 0, data.Length);

                tcp.ReceiveTimeout = 2000;
                using (var reader = new StreamReader(stream, Encoding.ASCII))
                    return await reader.ReadToEndAsync();
            }
        }
    }
}

[Test]
public async Task MissingHostHeader_Returns400()
{
    string request = "GET /hello.html HTTP/1.1\r\nConnection: close\r\n\r\n";
    string response = await RawHttp.SendAsync(server.Settings.Port, request);
    StringAssert.StartsWith("HTTP/1.1 400", response);
}
```

### 13.5 Settings / config-section tests

```csharp
[Test]
public void LoadFromConfig_MatchesProgrammaticSettings()
{
    // app.config in the test project defines a <SimpleHttpServer> section with
    // known values (port=9123, directory=".\testroot\", serviceUrlPrefix="/srvc").
    var fromConfig = SimpleHttpServerSettings.LoadFromConfig("SimpleHttpServer");

    Assert.AreEqual(9123, fromConfig.Port);
    Assert.AreEqual("/srvc", fromConfig.ServiceUrlPrefix);
}

[Test]
public void CreateDefault_ProducesEnabledServerOnDefaultPort()
{
    var settings = SimpleHttpServerSettings.CreateDefault();
    Assert.IsTrue(settings.Enabled);
    Assert.Greater(settings.Port, 0);
}
```

### 13.6 Optional tooling

- **Load/soak**: a quick `k6` or `bombardier` script (or just the `ConcurrentRequests`
  test above scaled up) to confirm the server survives a burst of requests without
  leaking threads/handles across repeated `Start()`/`Stop()` cycles.
- **Static analysis**: run the repo's existing analyzers/`dotnet format` against the new
  project; no new tooling required.
