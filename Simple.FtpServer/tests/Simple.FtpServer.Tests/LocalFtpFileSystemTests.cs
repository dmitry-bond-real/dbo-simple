using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Simple.FtpServer;

namespace Simple.FtpServer.Tests
{
    public sealed class LocalFtpFileSystemTests
    {
        [Test]
        public void NormalizePath_ResolvesVirtualPathsAndRejectsTraversal()
        {
            var user = CreateUser("alice", Path.GetTempPath(), FtpPermission.List);
            var fileSystem = new LocalFtpFileSystem(Path.Combine(Path.GetTempPath(), "ftp-staging"));

            Assert.That(fileSystem.NormalizePath(user, "/reports", "annual report.txt"),
                Is.EqualTo("/reports/annual report.txt"));
            Assert.That(fileSystem.NormalizePath(user, "/reports", "/reports/../notes.txt"),
                Is.EqualTo("/notes.txt"));
            Assert.Throws<UnauthorizedAccessException>(() => fileSystem.NormalizePath(user, "/", "../../outside"));
            Assert.Throws<InvalidDataException>(() => fileSystem.NormalizePath(user, "/", "C:/outside"));
        }

        [Test]
        public void BeginUpload_StagesUntilCommitAndPreservesTargetWhenDisposed()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var home = Path.Combine(root, "home");
            var staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(home);
            try
            {
                var user = CreateUser("alice", home, FtpPermission.Write);
                var fileSystem = new LocalFtpFileSystem(staging);
                fileSystem.Initialize(new[] { user });

                using (var upload = fileSystem.BeginUpload(user, "/payload.bin"))
                {
                    upload.Stream.WriteByte(42);
                    Assert.That(File.Exists(Path.Combine(home, "payload.bin")), Is.False);
                    upload.Commit();
                }

                Assert.That(File.ReadAllBytes(Path.Combine(home, "payload.bin")), Is.EqualTo(new byte[] { 42 }));
                using (var failedUpload = fileSystem.BeginUpload(user, "/payload.bin"))
                {
                    failedUpload.Stream.WriteByte(99);
                }

                Assert.That(File.ReadAllBytes(Path.Combine(home, "payload.bin")), Is.EqualTo(new byte[] { 42 }));
                Assert.That(Directory.GetFiles(staging).Any(), Is.False);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static FtpUserAccount CreateUser(string username, string home, params FtpPermission[] permissions)
        {
            return new FtpUserAccount(username, true, home, permissions, 1,
                "PBKDF2-HMAC-SHA256", 600000, new byte[16], new byte[32]);
        }
    }
}