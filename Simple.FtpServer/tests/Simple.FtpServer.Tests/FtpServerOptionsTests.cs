using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Simple.FtpServer;

namespace Simple.FtpServer.Tests
{
    public sealed class FtpServerOptionsTests
    {
        [Test]
        public void Validate_AcceptsLoopbackDefaultsWithRequiredPaths()
        {
            var root = Path.GetTempPath();
            var options = new FtpServerOptions
            {
                UsersFilePath = Path.Combine(root, "users.ini"),
                StagingDirectoryPath = Path.Combine(root, "staging")
            };

            Assert.DoesNotThrow(() => options.Validate());
        }

        [Test]
        public void Validate_RejectsNonLoopbackWithoutAcknowledgment()
        {
            var options = new FtpServerOptions
            {
                ControlBindAddress = IPAddress.Parse("192.0.2.1"),
                UsersFilePath = Path.Combine(Path.GetTempPath(), "users.ini"),
                StagingDirectoryPath = Path.Combine(Path.GetTempPath(), "staging")
            };

            Assert.Throws<ArgumentException>(() => options.Validate());
        }

        [Test]
        public void Validate_RejectsOverlappingControlAndPassivePorts()
        {
            var options = new FtpServerOptions
            {
                ControlPort = 50010,
                UsersFilePath = Path.Combine(Path.GetTempPath(), "users.ini"),
                StagingDirectoryPath = Path.Combine(Path.GetTempPath(), "staging")
            };

            Assert.Throws<ArgumentException>(() => options.Validate());
        }

        [Test]
        public async Task IniStore_CreatesHashedAccountAndAuthenticates()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var home = Path.Combine(root, "home");
            var database = Path.Combine(root, "users.ini");
            Directory.CreateDirectory(home);
            try
            {
                var store = new IniFtpUserStore(database);
                await store.CreateUserAsync("alice", "correct horse battery", home,
                    new[] { FtpPermission.List, FtpPermission.Read }, CancellationToken.None);
                var contents = File.ReadAllText(database);
                Assert.That(contents, Does.Not.Contain("correct horse battery"));
                Assert.That(contents, Does.Contain("PasswordSalt="));
                Assert.That(contents, Does.Contain("PasswordHash="));
                Assert.That((await store.AuthenticateAsync("alice", "correct horse battery", CancellationToken.None)).Username,
                    Is.EqualTo("alice"));
                Assert.That(await store.AuthenticateAsync("alice", "wrong password here", CancellationToken.None), Is.Null);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public async Task IniStore_FailedReloadPreservesLastValidSnapshot()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var home = Path.Combine(root, "home");
            var database = Path.Combine(root, "users.ini");
            Directory.CreateDirectory(home);
            try
            {
                var store = new IniFtpUserStore(database);
                await store.CreateUserAsync("alice", "correct horse battery", home,
                    new[] { FtpPermission.List }, CancellationToken.None);
                File.WriteAllText(database, "[user:alice]\nEnabled=maybe\n");
                Assert.ThrowsAsync<InvalidDataException>(() => store.ReloadAsync(CancellationToken.None));
                Assert.That(store.Users, Has.Count.EqualTo(1));
                Assert.That((await store.AuthenticateAsync("alice", "correct horse battery", CancellationToken.None)).Username,
                    Is.EqualTo("alice"));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public async Task ControlLineReader_ReadsFragmentedAndCoalescedCommands()
        {
            var reader = new ControlLineReader();
            using (var stream = new FragmentedStream(Encoding.UTF8.GetBytes("USER alice\r\nPWD\r\n")))
            {
                Assert.That(await reader.ReadLineAsync(stream, CancellationToken.None), Is.EqualTo("USER alice"));
                Assert.That(await reader.ReadLineAsync(stream, CancellationToken.None), Is.EqualTo("PWD"));
                Assert.That(await reader.ReadLineAsync(stream, CancellationToken.None), Is.Null);
            }
        }

        [Test]
        public void ControlLineReader_RejectsOversizedAndMalformedUtf8Commands()
        {
            var reader = new ControlLineReader();
            using (var oversized = new MemoryStream(Encoding.ASCII.GetBytes(new string('A', 4095) + "\r\n")))
            {
                Assert.ThrowsAsync<FtpCommandTooLongException>(() => reader.ReadLineAsync(oversized, CancellationToken.None));
            }

            using (var malformed = new MemoryStream(new byte[] { 0xC3, 0x28, 0x0D, 0x0A }))
            {
                Assert.ThrowsAsync<FtpProtocolException>(() => reader.ReadLineAsync(malformed, CancellationToken.None));
            }
        }

        private sealed class FragmentedStream : MemoryStream
        {
            public FragmentedStream(byte[] bytes) : base(bytes)
            {
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return base.ReadAsync(buffer, offset, Math.Min(2, count), cancellationToken);
            }
        }
    }
}