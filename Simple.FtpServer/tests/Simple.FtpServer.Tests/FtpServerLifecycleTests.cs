using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Simple.FtpServer;

namespace Simple.FtpServer.Tests
{
    public sealed class FtpServerLifecycleTests
    {
        [Test]
        public async Task StopIsIdempotentAndServerCanRestart()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var home = Path.Combine(root, "home");
            var staging = Path.Combine(root, "staging");
            var usersPath = Path.Combine(root, "users.ini");
            Directory.CreateDirectory(home);
            var users = new IniFtpUserStore(usersPath);
            await users.CreateUserAsync("alice", "correct horse battery", home,
                new[] { FtpPermission.List }, CancellationToken.None);
            var options = new FtpServerOptions
            {
                ControlPort = GetAvailablePort(),
                UsersFilePath = usersPath,
                StagingDirectoryPath = staging
            };
            var server = new FtpServer(options, users, new LocalFtpFileSystem(staging), null);
            try
            {
                await server.StartAsync(CancellationToken.None);
                Assert.That(server.IsRunning, Is.True);
                await AssertGreetingAsync(options.ControlPort);
                await server.StopAsync(CancellationToken.None);
                await server.StopAsync(CancellationToken.None);
                Assert.That(server.IsRunning, Is.False);

                await server.StartAsync(CancellationToken.None);
                Assert.That(server.IsRunning, Is.True);
                await AssertGreetingAsync(options.ControlPort);
            }
            finally
            {
                await server.StopAsync(CancellationToken.None);
                server.Dispose();
                server.Dispose();
                Directory.Delete(root, true);
            }
        }

        [Test]
        public async Task SessionLimitRejectsAdditionalConnectionsWith421()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var usersPath = Path.Combine(root, "users.ini");
            File.WriteAllText(usersPath, string.Empty);
            var staging = Path.Combine(root, "staging");
            var options = new FtpServerOptions
            {
                ControlPort = GetAvailablePort(),
                MaximumControlSessions = 1,
                UsersFilePath = usersPath,
                StagingDirectoryPath = staging
            };
            var server = new FtpServer(options, new IniFtpUserStore(usersPath),
                new LocalFtpFileSystem(staging), null);
            try
            {
                await server.StartAsync(CancellationToken.None);
                using (var first = new TcpClient())
                using (var second = new TcpClient())
                {
                    await first.ConnectAsync(IPAddress.Loopback, options.ControlPort);
                    using (var firstReader = new StreamReader(first.GetStream(), Encoding.ASCII))
                    {
                        Assert.That(await firstReader.ReadLineAsync(), Does.StartWith("220"));
                        await second.ConnectAsync(IPAddress.Loopback, options.ControlPort);
                        using (var secondReader = new StreamReader(second.GetStream(), Encoding.ASCII))
                        {
                            Assert.That(await secondReader.ReadLineAsync(), Is.EqualTo("421 Too many sessions."));
                        }
                    }
                }
            }
            finally
            {
                await server.StopAsync(CancellationToken.None);
                server.Dispose();
                Directory.Delete(root, true);
            }
        }

        private static async Task AssertGreetingAsync(int port)
        {
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                using (var reader = new StreamReader(client.GetStream(), Encoding.ASCII))
                {
                    Assert.That(await reader.ReadLineAsync(), Does.StartWith("220"));
                }
            }
        }

        private static int GetAvailablePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}