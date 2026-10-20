using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Simple.HttpServer.Tests
{
    // Every test runs once per engine (Socket and HttpListener) to satisfy spec §11.10.
    [TestFixture(HttpServerEngineKind.Socket)]
    [TestFixture(HttpServerEngineKind.HttpListener)]
    public class SimpleHttpServerIntegrationTests
    {
        private readonly HttpServerEngineKind _engine;
        private string _root;
        private SimpleHttpServer _server;
        private HttpClient _client;

        public SimpleHttpServerIntegrationTests(HttpServerEngineKind pEngine)
        {
            _engine = pEngine;
        }

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "httptest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "hello.html"), "<html>hi</html>");

            var settings = SimpleHttpServerSettings.CreateDefault();
            settings.Engine = _engine;
            settings.Port = TestPorts.GetFreeTcpPort();
            settings.Directory = _root;

            _server = new SimpleHttpServer(settings);
            _server.Start();

            // UseCookies=false so manually-set "Cookie" request headers round-trip unmodified in tests.
            var handler = new HttpClientHandler { UseCookies = false };
            _client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:" + settings.Port + "/") };
        }

        [TearDown]
        public void TearDown()
        {
            _client.Dispose();
            _server.Stop();
            _server.Dispose();
            Directory.Delete(_root, true);
        }

        [Test]
        public async Task StaticFile_Exists_Returns200_WithCorrectContentType()
        {
            var resp = await _client.GetAsync("hello.html");
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            Assert.AreEqual("text/html", resp.Content.Headers.ContentType.MediaType);
        }

        [Test]
        public async Task StaticFile_Missing_Returns404()
        {
            var resp = await _client.GetAsync("missing.html");
            Assert.AreEqual(HttpStatusCode.NotFound, resp.StatusCode);
        }

        [Test]
        public async Task PathTraversal_IsRejected()
        {
            var resp = await _client.GetAsync("../../../windows/win.ini");
            Assert.That(resp.StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound).Or.EqualTo(HttpStatusCode.Forbidden));
        }

        [Test]
        public async Task ServiceHandler_LongestPrefixWins()
        {
            _server.AddServiceRequestHandler("/srvc", ctx =>
            { ctx.WriteResponse(200, "text/plain", Encoding.UTF8.GetBytes("short")); return true; });
            _server.AddServiceRequestHandler("/srvc/ping", ctx =>
            { ctx.WriteResponse(200, "text/plain", Encoding.UTF8.GetBytes("pong")); return true; });

            string body = await _client.GetStringAsync("srvc/ping");
            Assert.AreEqual("pong", body);
        }

        [Test]
        public async Task AddServiceRequestHandler_SamePrefixTwice_ReplacesHandler()
        {
            _server.AddServiceRequestHandler("/srvc/x", ctx =>
            { ctx.WriteResponse(200, "text/plain", Encoding.UTF8.GetBytes("first")); return true; });
            _server.AddServiceRequestHandler("/srvc/x", ctx =>
            { ctx.WriteResponse(200, "text/plain", Encoding.UTF8.GetBytes("second")); return true; });

            string body = await _client.GetStringAsync("srvc/x");
            Assert.AreEqual("second", body);
        }

        [Test]
        public async Task RemoveServiceRequestHandler_StopsRouting()
        {
            SimpleHttpServer.HandleServiceRequestMethod handler = ctx =>
            { ctx.WriteResponse(200, "text/plain", Encoding.UTF8.GetBytes("v1")); return true; };
            _server.AddServiceRequestHandler("/srvc/x", handler);
            _server.RemoveServiceRequestHandler(handler);

            var resp = await _client.GetAsync("srvc/x");
            Assert.AreEqual(HttpStatusCode.NotFound, resp.StatusCode);
        }

        [Test]
        public async Task DefaultPage_IsServed_ForRootUrl()
        {
            _server.Settings.DefaultPage = "/hello.html";
            var resp = await _client.GetAsync("");
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        }

        [Test]
        public async Task UnknownMethod_Returns405()
        {
            var req = new HttpRequestMessage(new HttpMethod("TRACE"), "hello.html");
            var resp = await _client.SendAsync(req);
            Assert.AreEqual(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
        }

        [Test]
        public async Task UnhandledHandlerException_Returns500_WithoutStackTrace()
        {
            _server.AddServiceRequestHandler("/srvc/boom", ctx => throw new InvalidOperationException("boom"));

            var resp = await _client.GetAsync("srvc/boom");
            string body = await resp.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
            StringAssert.DoesNotContain("InvalidOperationException", body);
            StringAssert.DoesNotContain("at Simple.HttpServer", body);
        }

        [Test]
        public async Task ConcurrentRequests_AllSucceed()
        {
            var tasks = Enumerable.Range(0, 25).Select(_ => _client.GetAsync("hello.html"));
            var results = await Task.WhenAll(tasks);
            Assert.IsTrue(results.All(r => r.StatusCode == HttpStatusCode.OK));
        }

        [Test]
        public void StartStop_IsIdempotent()
        {
            _server.Start(); // already running -> no-op, must not throw
            Assert.IsTrue(_server.IsRunning);

            _server.Stop();
            _server.Stop(); // already stopped -> no-op, must not throw
            Assert.IsFalse(_server.IsRunning);
        }

        [Test]
        public async Task MissingHostHeader_Returns400()
        {
            string request = "GET /hello.html HTTP/1.1\r\nConnection: close\r\n\r\n";
            string response = await RawHttp.SendAsync(_server.Settings.Port, request);
            StringAssert.StartsWith("HTTP/1.1 400", response);
        }

        [Test]
        public async Task Cookies_AreParsedFromRequestHeader()
        {
            Dictionary<string, string> seen = null;
            _server.AddServiceRequestHandler("/srvc/cookie", ctx =>
            {
                seen = ctx.Cookies;
                ctx.WriteResponse(200, "text/plain", Encoding.UTF8.GetBytes("ok"));
                return true;
            });

            var req = new HttpRequestMessage(HttpMethod.Get, "srvc/cookie");
            req.Headers.Add("Cookie", "a=1; b=2");
            await _client.SendAsync(req);

            Assert.IsNotNull(seen);
            Assert.AreEqual("1", seen["a"]);
            Assert.AreEqual("2", seen["b"]);
        }

        [Test]
        public async Task FormParameters_AreParsedFromUrlEncodedBody()
        {
            Dictionary<string, string> seen = null;
            _server.AddServiceRequestHandler("/srvc/form", ctx =>
            {
                seen = ctx.FormParameters;
                ctx.WriteResponse(200, "text/plain", Encoding.UTF8.GetBytes("ok"));
                return true;
            });

            var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("x", "hello world") });
            await _client.PostAsync("srvc/form", content);

            Assert.IsNotNull(seen);
            Assert.AreEqual("hello world", seen["x"]);
        }
    }
}
