using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Simple.FtpServer;

namespace Simple.FtpServer.Tests
{
    public sealed class IniFtpUserStoreTests
    {
        [Test]
        public async Task UpdateUserAsync_PreservesCredentialsAndPersistsNewAccountState()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var home = Path.Combine(root, "home");
            Directory.CreateDirectory(home);
            try
            {
                var path = Path.Combine(root, "users.ini");
                var store = new IniFtpUserStore(path);
                await store.CreateUserAsync("alice", "correct horse battery", home,
                    new[] { FtpPermission.List, FtpPermission.Read }, CancellationToken.None);
                await store.UpdateUserAsync("alice", false, home,
                    new[] { FtpPermission.List }, CancellationToken.None);

                Assert.That(store.Users.Single().Enabled, Is.False);
                Assert.That(store.Users.Single().Permissions, Is.EquivalentTo(new[] { FtpPermission.List }));
                Assert.That(await store.AuthenticateAsync("alice", "correct horse battery", CancellationToken.None), Is.Null);

                var reloaded = new IniFtpUserStore(path);
                await reloaded.ReloadAsync(CancellationToken.None);
                Assert.That(reloaded.Users.Single().Enabled, Is.False);
                Assert.That(reloaded.Users.Single().Permissions, Is.EquivalentTo(new[] { FtpPermission.List }));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public async Task CreateUserAsync_RejectsOverlappingHomesWithoutChangingDatabase()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var home = Path.Combine(root, "home");
            var childHome = Path.Combine(home, "child");
            Directory.CreateDirectory(childHome);
            try
            {
                var store = new IniFtpUserStore(Path.Combine(root, "users.ini"));
                await store.CreateUserAsync("alice", "correct horse battery", home,
                    new[] { FtpPermission.List }, CancellationToken.None);
                Assert.ThrowsAsync<InvalidDataException>(() => store.CreateUserAsync("bob",
                    "another correct phrase", childHome, new[] { FtpPermission.List }, CancellationToken.None));
                Assert.That(store.Users, Has.Count.EqualTo(1));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}