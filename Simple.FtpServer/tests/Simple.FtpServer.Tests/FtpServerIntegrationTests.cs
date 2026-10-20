using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Simple.FtpServer;

namespace Simple.FtpServer.Tests
{
    public sealed class FtpServerIntegrationTests
    {
        [Test]
        public async Task Server_AuthenticatesAndTransfersBinaryFilesInPassiveMode()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var home = Path.Combine(root, "home");
            var staging = Path.Combine(root, "staging");
            var usersPath = Path.Combine(root, "users.ini");
            Directory.CreateDirectory(home);
            var users = new IniFtpUserStore(usersPath);
            await users.CreateUserAsync("alice", "correct horse battery", home,
                new[] { FtpPermission.List, FtpPermission.Read, FtpPermission.Write, FtpPermission.Delete,
                    FtpPermission.Rename }, CancellationToken.None);
            var controlPort = GetAvailablePort();
            var passivePort = GetAvailablePort();
            while (passivePort == controlPort)
            {
                passivePort = GetAvailablePort();
            }

            var options = new FtpServerOptions
            {
                ControlPort = controlPort,
                PassivePortStart = passivePort,
                PassivePortEnd = passivePort,
                UsersFilePath = usersPath,
                StagingDirectoryPath = staging
            };
            var server = new FtpServer(options, users, new LocalFtpFileSystem(staging), null);
            try
            {
                await server.StartAsync(CancellationToken.None);
                using (var control = new TcpClient())
                {
                    await control.ConnectAsync(IPAddress.Loopback, options.ControlPort);
                    using (var reader = new StreamReader(control.GetStream(), new UTF8Encoding(false)))
                    using (var writer = new StreamWriter(control.GetStream(), new UTF8Encoding(false)) { AutoFlush = true })
                    {
                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("220"));
                        await writer.WriteLineAsync("FEAT");
                        Assert.That(await reader.ReadLineAsync(), Is.EqualTo("211-Features"));
                        var features = new StringBuilder();
                        string featureLine;
                        do
                        {
                            featureLine = await reader.ReadLineAsync();
                            features.AppendLine(featureLine);
                        } while (featureLine != "211 End");
                        Assert.That(features.ToString(), Does.Contain("UTF8"));

                        await writer.WriteLineAsync("PWD");
                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("530"));
                        await writer.WriteLineAsync("USER missing");
                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("331"));
                        await writer.WriteLineAsync("PASS incorrect password");
                        var unknownFailure = await reader.ReadLineAsync();
                        Assert.That(unknownFailure, Is.EqualTo("530 Login incorrect."));
                        await writer.WriteLineAsync("USER alice");
                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("331"));
                        await writer.WriteLineAsync("PASS incorrect password");
                        var wrongPasswordFailure = await reader.ReadLineAsync();
                        Assert.That(wrongPasswordFailure, Is.EqualTo(unknownFailure));
                        await writer.WriteLineAsync("USER alice");
                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("331"));
                        await writer.WriteLineAsync("PASS correct horse battery");
                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("230"));
                        await writer.WriteLineAsync("TYPE I");
                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("200"));

                        await NegotiatePassiveAsync(reader, writer);
                        await NegotiatePassiveAsync(reader, writer);
                        var source = Enumerable.Range(0, 4097).Select(value => (byte)(value % 251)).ToArray();
                        await UploadPassiveAsync(reader, writer, source);
                        await writer.WriteLineAsync("SIZE payload report-雪.bin");
                        Assert.That(await reader.ReadLineAsync(), Is.EqualTo("213 4097"));
                        var downloaded = await DownloadPassiveAsync(reader, writer, true);
                        Assert.That(downloaded, Is.EqualTo(source));
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

        [TestCase(false)]
        [TestCase(true)]
        public async Task Server_SupportsActivePortAndEprt(bool extended)
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var home = Path.Combine(root, "home");
            var staging = Path.Combine(root, "staging");
            var usersPath = Path.Combine(root, "users.ini");
            Directory.CreateDirectory(home);
            var payload = new byte[] { 0, 1, 2, 13, 10, 255 };
            File.WriteAllBytes(Path.Combine(home, "active.bin"), payload);
            var users = new IniFtpUserStore(usersPath);
            await users.CreateUserAsync("alice", "correct horse battery", home,
                new[] { FtpPermission.Read, FtpPermission.Write }, CancellationToken.None);
            var options = new FtpServerOptions
            {
                ControlPort = GetAvailablePort(),
                PassivePortStart = 54100,
                PassivePortEnd = 54110,
                UsersFilePath = usersPath,
                StagingDirectoryPath = staging
            };
            var server = new FtpServer(options, users, new LocalFtpFileSystem(staging), null);
            try
            {
                await server.StartAsync(CancellationToken.None);
                using (var control = new TcpClient())
                {
                    var dataListener = new TcpListener(IPAddress.Loopback, 0);
                    dataListener.Start();
                    try
                    {
                    await control.ConnectAsync(IPAddress.Loopback, options.ControlPort);
                    using (var reader = new StreamReader(control.GetStream(), Encoding.ASCII))
                    using (var writer = new StreamWriter(control.GetStream(), Encoding.ASCII) { AutoFlush = true })
                    {
                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("220"));
                        await writer.WriteLineAsync("USER alice");
                        await reader.ReadLineAsync();
                        await writer.WriteLineAsync("PASS correct horse battery");
                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("230"));

                        var port = ((IPEndPoint)dataListener.LocalEndpoint).Port;
                        if (extended)
                        {
                            await writer.WriteLineAsync("EPRT |1|127.0.0.1|" + port + "|");
                        }
                        else
                        {
                            await writer.WriteLineAsync("PORT 127,0,0,1," + (port / 256) + "," + (port % 256));
                        }

                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("200"));
                        await writer.WriteLineAsync("RETR active.bin");
                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("150"));
                        using (var data = await dataListener.AcceptTcpClientAsync())
                        using (var output = new MemoryStream())
                        {
                            await data.GetStream().CopyToAsync(output);
                            Assert.That(output.ToArray(), Is.EqualTo(payload));
                        }

                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("226"));
                        await writer.WriteLineAsync(extended
                            ? "EPRT |1|127.0.0.1|" + port + "|"
                            : "PORT 127,0,0,1," + (port / 256) + "," + (port % 256));
                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("200"));
                        await writer.WriteLineAsync("STOR active-upload.bin");
                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("150"));
                        using (var data = await dataListener.AcceptTcpClientAsync())
                        {
                            await data.GetStream().WriteAsync(payload, 0, payload.Length);
                            data.Client.Shutdown(SocketShutdown.Send);
                        }

                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("226"));
                    }

                    Assert.That(File.ReadAllBytes(Path.Combine(home, "active-upload.bin")), Is.EqualTo(payload));
                    }
                    finally
                    {
                        dataListener.Stop();
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

        [Test]
        public void AsciiConverter_TranslatesCrSequencesAcrossBufferBoundaries()
        {
            var upload = new AsciiTransferConverter(true);
            var uploadBytes = upload.Convert(new byte[] { (byte)'A', (byte)'\r' }, 0, 2, false)
                .Concat(upload.Convert(new byte[] { (byte)'\n', (byte)'B', (byte)'\r', 0, (byte)'C' }, 0, 5, true))
                .ToArray();
            Assert.That(uploadBytes, Is.EqualTo(new byte[] { (byte)'A', (byte)'\r', (byte)'\n', (byte)'B',
                (byte)'\r', (byte)'C' }));

            var download = new AsciiTransferConverter(false);
            var downloadBytes = download.Convert(new byte[] { (byte)'A', (byte)'\r' }, 0, 2, false)
                .Concat(download.Convert(new byte[] { (byte)'\n', (byte)'B', (byte)'\r', (byte)'C' }, 0, 4, true))
                .ToArray();
            Assert.That(downloadBytes, Is.EqualTo(new byte[] { (byte)'A', (byte)'\r', (byte)'\n', (byte)'B',
                (byte)'\r', 0, (byte)'C' }));
        }

        [Test]
        public async Task ReadOnlyAccount_CannotModifyStorage()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var home = Path.Combine(root, "home");
            var staging = Path.Combine(root, "staging");
            var usersPath = Path.Combine(root, "users.ini");
            Directory.CreateDirectory(home);
            var protectedFile = Path.Combine(home, "protected.txt");
            File.WriteAllText(protectedFile, "unchanged");
            var users = new IniFtpUserStore(usersPath);
            await users.CreateUserAsync("reader", "correct horse battery", home,
                new[] { FtpPermission.List, FtpPermission.Read }, CancellationToken.None);
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
                using (var control = new TcpClient())
                {
                    await control.ConnectAsync(IPAddress.Loopback, options.ControlPort);
                    using (var reader = new StreamReader(control.GetStream(), Encoding.ASCII))
                    using (var writer = new StreamWriter(control.GetStream(), Encoding.ASCII) { AutoFlush = true })
                    {
                        await reader.ReadLineAsync();
                        await writer.WriteLineAsync("USER reader");
                        await reader.ReadLineAsync();
                        await writer.WriteLineAsync("PASS correct horse battery");
                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("230"));

                        await writer.WriteLineAsync("STOR protected.txt");
                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("550"));
                        await writer.WriteLineAsync("DELE protected.txt");
                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("550"));
                        await writer.WriteLineAsync("MKD new-directory");
                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("550"));
                    }
                }

                Assert.That(File.ReadAllText(protectedFile), Is.EqualTo("unchanged"));
                Assert.That(Directory.Exists(Path.Combine(home, "new-directory")), Is.False);
            }
            finally
            {
                await server.StopAsync(CancellationToken.None);
                server.Dispose();
                Directory.Delete(root, true);
            }
        }

        [Test]
        public async Task ClosingControlConnectionDuringTransfer_ReclaimsPassiveListener()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var home = Path.Combine(root, "home");
            var staging = Path.Combine(root, "staging");
            var usersPath = Path.Combine(root, "users.ini");
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "idle.txt"), "content");
            var users = new IniFtpUserStore(usersPath);
            await users.CreateUserAsync("reader", "correct horse battery", home,
                new[] { FtpPermission.Read }, CancellationToken.None);
            var options = new FtpServerOptions
            {
                ControlPort = GetAvailablePort(),
                PassivePortStart = 54200,
                PassivePortEnd = 54210,
                UsersFilePath = usersPath,
                StagingDirectoryPath = staging
            };
            var server = new FtpServer(options, users, new LocalFtpFileSystem(staging), null);
            try
            {
                await server.StartAsync(CancellationToken.None);
                var passivePort = 0;
                using (var control = new TcpClient())
                {
                    await control.ConnectAsync(IPAddress.Loopback, options.ControlPort);
                    using (var reader = new StreamReader(control.GetStream(), Encoding.ASCII))
                    using (var writer = new StreamWriter(control.GetStream(), Encoding.ASCII) { AutoFlush = true })
                    {
                        await reader.ReadLineAsync();
                        await writer.WriteLineAsync("USER reader");
                        await reader.ReadLineAsync();
                        await writer.WriteLineAsync("PASS correct horse battery");
                        await reader.ReadLineAsync();
                        passivePort = await NegotiatePassiveAsync(reader, writer);
                        await writer.WriteLineAsync("RETR idle.txt");
                        Assert.That(await reader.ReadLineAsync(), Does.StartWith("150"));
                    }
                }

                await Task.Delay(300);
                using (var data = new TcpClient())
                {
                    Assert.ThrowsAsync<SocketException>(async () =>
                        await data.ConnectAsync(IPAddress.Loopback, passivePort));
                }
            }
            finally
            {
                await server.StopAsync(CancellationToken.None);
                server.Dispose();
                Directory.Delete(root, true);
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

        private static async Task UploadPassiveAsync(StreamReader reader, StreamWriter writer, byte[] bytes)
        {
            var port = await NegotiatePassiveAsync(reader, writer);
            using (var data = new TcpClient())
            {
                await data.ConnectAsync(IPAddress.Loopback, port);
                await writer.WriteLineAsync("STOR payload report-雪.bin");
                Assert.That(await reader.ReadLineAsync(), Does.StartWith("150"));
                await data.GetStream().WriteAsync(bytes, 0, bytes.Length);
                data.Client.Shutdown(SocketShutdown.Send);
                Assert.That(await reader.ReadLineAsync(), Does.StartWith("226"));
            }
        }

        private static async Task<byte[]> DownloadPassiveAsync(StreamReader reader, StreamWriter writer, bool extended)
        {
            var port = await NegotiatePassiveAsync(reader, writer, extended);
            using (var data = new TcpClient())
            {
                await data.ConnectAsync(IPAddress.Loopback, port);
                await writer.WriteLineAsync("RETR payload report-雪.bin");
                Assert.That(await reader.ReadLineAsync(), Does.StartWith("150"));
                using (var output = new MemoryStream())
                {
                    await data.GetStream().CopyToAsync(output);
                    Assert.That(await reader.ReadLineAsync(), Does.StartWith("226"));
                    return output.ToArray();
                }
            }
        }

        private static async Task<int> NegotiatePassiveAsync(StreamReader reader, StreamWriter writer, bool extended = false)
        {
            await writer.WriteLineAsync(extended ? "EPSV" : "PASV");
            var response = await reader.ReadLineAsync();
            Assert.That(response, Does.StartWith(extended ? "229" : "227"));
            if (extended)
            {
                var portText = response.Substring(response.IndexOf('(') + 1).TrimEnd(')', '.').Trim('|');
                return int.Parse(portText, System.Globalization.CultureInfo.InvariantCulture);
            }

            var numbers = response.Substring(response.IndexOf('(') + 1).TrimEnd(')', '.')
                .Split(',').Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            return numbers[4] * 256 + numbers[5];
        }
    }
}