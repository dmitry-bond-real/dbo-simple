using NUnit.Framework;

namespace Simple.HttpServer.Tests
{
    [TestFixture]
    public class SimpleHttpServerSettingsTests
    {
        [Test]
        public void LoadFromConfig_MatchesProgrammaticSettings()
        {
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

        [Test]
        public void CreateDefault_SeedsMimeTypesAndReplyCodes()
        {
            var settings = SimpleHttpServerSettings.CreateDefault();
            Assert.That(settings.MimeTypes["html"], Is.EqualTo("text/html"));
            Assert.That(settings.HttpReplyCodes["404"], Is.EqualTo("Not Found"));
        }
    }
}
