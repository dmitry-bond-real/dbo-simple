using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Simple.FtpServer
{
    public interface IFtpUserStore
    {
        IReadOnlyCollection<FtpUserAccount> Users { get; }
        Task ReloadAsync(CancellationToken cancellationToken);
        Task<FtpUserAccount> AuthenticateAsync(string username, string password, CancellationToken cancellationToken);
        Task CreateUserAsync(string username, string password, string homeDirectory,
            IEnumerable<FtpPermission> permissions, CancellationToken cancellationToken);
        Task UpdateUserAsync(string username, bool enabled, string homeDirectory,
            IEnumerable<FtpPermission> permissions, CancellationToken cancellationToken);
        Task SetEnabledAsync(string username, bool enabled, CancellationToken cancellationToken);
        Task ChangePasswordAsync(string username, string newPassword, CancellationToken cancellationToken);
    }
}