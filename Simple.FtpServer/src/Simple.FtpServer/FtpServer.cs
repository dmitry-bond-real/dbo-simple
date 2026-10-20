using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Simple.FtpServer
{
    public sealed class FtpServer : IDisposable
    {
        private readonly FtpServerOptions _options;
        private readonly IFtpUserStore _userStore;
        private readonly IFtpFileSystem _fileSystem;
        private readonly IFtpLogSink _logSink;
        private readonly SemaphoreSlim _lifecycle = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, FtpControlSession> _sessions =
            new ConcurrentDictionary<string, FtpControlSession>();
        private TcpListener _listener;
        private CancellationTokenSource _runCancellation;
        private Task _acceptTask;
        private int _sessionCount;
        private bool _disposed;

        public FtpServer(FtpServerOptions options, IFtpUserStore userStore,
            IFtpFileSystem fileSystem, IFtpLogSink logSink)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _logSink = logSink;
        }

        public bool IsRunning { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            TcpListener listener = null;
            try
            {
                ThrowIfDisposed();
                if (IsRunning)
                {
                    throw new InvalidOperationException("The FTP server is already running.");
                }

                _options.Validate();
                await _userStore.ReloadAsync(cancellationToken).ConfigureAwait(false);
                ValidateDatabaseOutsideUserHomes(_options.UsersFilePath, _userStore.Users);
                _fileSystem.Initialize(_userStore.Users);
                cancellationToken.ThrowIfCancellationRequested();

                listener = new TcpListener(_options.ControlBindAddress, _options.ControlPort);
                listener.Start(_options.MaximumControlSessions);
                _listener = listener;
                _runCancellation = new CancellationTokenSource();
                IsRunning = true;
                if (!IPAddress.IsLoopback(_options.ControlBindAddress) ||
                    !IPAddress.IsLoopback(_options.EffectivePassiveBindAddress))
                {
                    Log(null, "security-warning", null, null,
                        "Plain FTP is bound to a non-loopback address.");
                }

                _acceptTask = AcceptLoopAsync(listener, _runCancellation.Token);
            }
            catch
            {
                listener?.Stop();
                _listener?.Stop();
                _listener = null;
                _runCancellation?.Dispose();
                _runCancellation = null;
                IsRunning = false;
                throw;
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsRunning)
                {
                    return;
                }

                IsRunning = false;
                _runCancellation.Cancel();
                _listener.Stop();
                _listener = null;
                foreach (var session in _sessions.Values)
                {
                    session.Cancel();
                }

                var deadline = DateTime.UtcNow + _options.ShutdownLimit;
                if (_acceptTask != null && !await WaitForShutdownAsync(_acceptTask, deadline).ConfigureAwait(false))
                {
                    ObserveTask(_acceptTask);
                }

                foreach (var session in _sessions.Values)
                {
                    session.Cancel();
                }

                var sessionTasks = _sessions.Values.Select(session => session.Completion).ToArray();
                var allSessions = Task.WhenAll(sessionTasks);
                if (!await WaitForShutdownAsync(allSessions, deadline).ConfigureAwait(false))
                {
                    foreach (var session in _sessions.Values)
                    {
                        session.Cancel();
                    }

                    ObserveTask(allSessions);
                }
                else
                {
                    try
                    {
                        await allSessions.ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        Log(null, "shutdown-error", null, null, exception.GetType().Name);
                    }
                }

                _runCancellation.Dispose();
                _runCancellation = null;
                _acceptTask = null;
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            _disposed = true;
            _lifecycle.Dispose();
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    continue;
                }

                if (Interlocked.Increment(ref _sessionCount) > _options.MaximumControlSessions)
                {
                    Interlocked.Decrement(ref _sessionCount);
                    await RejectBusyClientAsync(client).ConfigureAwait(false);
                    continue;
                }

                var id = Guid.NewGuid().ToString("N");
                var session = new FtpControlSession(id, client, _options, _userStore, _fileSystem,
                        _logSink, _runCancellation.Token, () =>
                    {
                        _sessions.TryRemove(id, out var ignored);
                        Interlocked.Decrement(ref _sessionCount);
                    });
                _sessions[id] = session;
                session.Start();
                    if (cancellationToken.IsCancellationRequested)
                    {
                        client.Close();
                        break;
                    }
            }
        }

        private static async Task RejectBusyClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var bytes = System.Text.Encoding.ASCII.GetBytes("421 Too many sessions.\r\n");
                    await client.GetStream().WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
        }

        private static async Task<bool> WaitForShutdownAsync(Task task, DateTime deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return task.IsCompleted;
            }

            return await Task.WhenAny(task, Task.Delay(remaining)).ConfigureAwait(false) == task;
        }

        private static void ObserveTask(Task task)
        {
            task.ContinueWith(completed =>
            {
                var ignored = completed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        internal void Log(string sessionId, string eventName, string remoteEndpoint,
            string virtualPath, string detail, long? bytes = null, TimeSpan? duration = null, int? replyCode = null)
        {
            if (_logSink == null)
            {
                return;
            }

            try
            {
                _logSink.Write(new FtpLogEntry
                {
                    TimestampUtc = DateTime.UtcNow,
                    SessionId = sessionId,
                    RemoteEndpoint = remoteEndpoint,
                    EventName = eventName,
                    VirtualPath = virtualPath,
                    Detail = detail,
                    BytesTransferred = bytes,
                    Duration = duration,
                    ReplyCode = replyCode
                });
            }
            catch (Exception)
            {
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FtpServer));
            }
        }

        private static void ValidateDatabaseOutsideUserHomes(string usersPath,
            IEnumerable<FtpUserAccount> users)
        {
            var databasePath = Path.GetFullPath(usersPath);
            foreach (var user in users)
            {
                var home = Path.GetFullPath(user.HomeDirectory).TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                var separator = home.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                    home.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                    ? string.Empty
                    : Path.DirectorySeparatorChar.ToString();
                if (string.Equals(home, databasePath, StringComparison.OrdinalIgnoreCase) ||
                    databasePath.StartsWith(home + separator, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The users database must be outside every FTP home directory.");
                }
            }
        }
    }
}