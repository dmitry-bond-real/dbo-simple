using NUnit.Framework;

namespace Simple.HttpServer.Tests
{
    // Pure-logic unit tests: no socket/engine involved (spec §13.1).
    [TestFixture]
    public class SimpleHttpServerUnitTests
    {
        [Test]
        public void RemoveServiceRequestHandler_UnknownDelegate_DoesNotThrow()
        {
            var server = new SimpleHttpServer(SimpleHttpServerSettings.CreateDefault());
            SimpleHttpServer.HandleServiceRequestMethod handler = ctx => true;

            Assert.DoesNotThrow(() => server.RemoveServiceRequestHandler(handler));
        }

        [Test]
        public void AllowedMethods_ContainsBasicHttpVerbs()
        {
            var server = new SimpleHttpServer(SimpleHttpServerSettings.CreateDefault());
            CollectionAssert.IsSubsetOf(new[] { "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS" }, server.AllowedMethods);
        }

        [Test]
        public void Constructor_WithMissingConfigSection_FallsBackToDefaults()
        {
            var server = new SimpleHttpServer("NoSuchSection");
            Assert.IsTrue(server.Settings.Enabled);
            Assert.Greater(server.Settings.Port, 0);
        }

        [Test]
        public void AddServiceRequestHandler_NullArguments_Throw()
        {
            var server = new SimpleHttpServer(SimpleHttpServerSettings.CreateDefault());
            Assert.Throws<System.ArgumentNullException>(() => server.AddServiceRequestHandler(null, ctx => true));
            Assert.Throws<System.ArgumentNullException>(() => server.AddServiceRequestHandler("/srvc", null));
        }
    }
}
