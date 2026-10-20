using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Simple.FtpServer
{
    public sealed class LocalFtpFileSystem : IFtpFileSystem
    {
        private const string StagingPrefix = "simple-ftpsrv-";
        private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
        private readonly string _stagingDirectory;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _pathLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private readonly SecurityIdentifier[] _authorizedOperators;

        public LocalFtpFileSystem(string stagingDirectory) : this(stagingDirectory, null)
        {
        }

        public LocalFtpFileSystem(string stagingDirectory, IEnumerable<SecurityIdentifier> authorizedOperators)
        {
            if (string.IsNullOrWhiteSpace(stagingDirectory) || !Path.IsPathRooted(stagingDirectory))
            {
                throw new ArgumentException("An absolute staging directory is required.", nameof(stagingDirectory));
            }

            _stagingDirectory = TrimEndingSeparator(Path.GetFullPath(stagingDirectory));
            _authorizedOperators = authorizedOperators == null ? new SecurityIdentifier[0] : authorizedOperators.ToArray();
        }

        public void Initialize(IEnumerable<FtpUserAccount> users)
        {
            var accounts = users.ToArray();
            FtpStorageSecurity.EnsureNoReparsePoints(_stagingDirectory);
            Directory.CreateDirectory(_stagingDirectory);
            FtpStorageSecurity.SecureDirectory(_stagingDirectory, _authorizedOperators);
            EnsureNoReparsePoints(_stagingDirectory, Path.GetPathRoot(_stagingDirectory));
            foreach (var account in accounts)
            {
                if (PathsOverlap(_stagingDirectory, account.HomeDirectory))
                {
                    throw new InvalidDataException("The staging directory must be outside all user homes.");
                }

                if (!string.Equals(Path.GetPathRoot(_stagingDirectory), Path.GetPathRoot(account.HomeDirectory),
                    PathComparison))
                {
                    throw new InvalidDataException("The staging directory and user homes must be on the same volume.");
                }
            }

            foreach (var file in Directory.GetFiles(_stagingDirectory, StagingPrefix + "*.tmp", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                {
                    File.Delete(file);
                }
            }
        }

        public string NormalizePath(FtpUserAccount user, string workingDirectory, string argument)
        {
            if (user == null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            if (argument == null)
            {
                argument = string.Empty;
            }

            if (argument.IndexOfAny(new[] { '\0', '\\', ':', '*', '?', '<', '>', '"', '|' }) >= 0 ||
                argument.StartsWith("//", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The virtual path is invalid.");
            }

            var segments = new List<string>();
            if (!argument.StartsWith("/", StringComparison.Ordinal))
            {
                AddSegments(segments, workingDirectory);
            }

            AddSegments(segments, argument);
            return segments.Count == 0 ? "/" : "/" + string.Join("/", segments);
        }

        public bool DirectoryExists(FtpUserAccount user, string virtualPath)
        {
            return Directory.Exists(Resolve(user, virtualPath, false));
        }

        public bool FileExists(FtpUserAccount user, string virtualPath)
        {
            return File.Exists(Resolve(user, virtualPath, false));
        }

        public IReadOnlyList<FtpFileEntry> GetEntries(FtpUserAccount user, string virtualPath)
        {
            Require(user, FtpPermission.List);
            var physical = Resolve(user, virtualPath, false);
            if (File.Exists(physical))
            {
                var file = new FileInfo(physical);
                return new[] { new FtpFileEntry(file.Name, false, file.Length, file.LastWriteTimeUtc) };
            }

            if (!Directory.Exists(physical))
            {
                throw new DirectoryNotFoundException();
            }

            var entries = new List<FtpFileEntry>();
            foreach (var path in Directory.GetFileSystemEntries(physical))
            {
                EnsureNoReparsePoints(path, user.HomeDirectory);
                var name = Path.GetFileName(path);
                if (name.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                {
                    continue;
                }

                var attributes = File.GetAttributes(path);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                entries.Add(new FtpFileEntry(name, isDirectory, isDirectory ? 0 : ((FileInfo)info).Length,
                    info.LastWriteTimeUtc));
            }

            return entries.AsReadOnly();
        }

        public Stream OpenRead(FtpUserAccount user, string virtualPath)
        {
            Require(user, FtpPermission.Read);
            var physical = Resolve(user, virtualPath, true);
            if (!File.Exists(physical))
            {
                throw new FileNotFoundException();
            }

            return new FileStream(physical, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.SequentialScan);
        }

        public long GetLength(FtpUserAccount user, string virtualPath)
        {
            Require(user, FtpPermission.Read);
            var physical = Resolve(user, virtualPath, true);
            if (!File.Exists(physical))
            {
                throw new FileNotFoundException();
            }

            return new FileInfo(physical).Length;
        }

        public DateTime GetLastWriteTimeUtc(FtpUserAccount user, string virtualPath)
        {
            Require(user, FtpPermission.Read);
            var physical = Resolve(user, virtualPath, true);
            if (!File.Exists(physical))
            {
                throw new FileNotFoundException();
            }

            return File.GetLastWriteTimeUtc(physical);
        }

        public IFtpUploadTransaction BeginUpload(FtpUserAccount user, string virtualPath)
        {
            Require(user, FtpPermission.Write);
            var physical = Resolve(user, virtualPath, false);
            if (IsRoot(user, virtualPath))
            {
                throw new UnauthorizedAccessException();
            }

            var parent = Path.GetDirectoryName(physical);
            if (!Directory.Exists(parent))
            {
                throw new DirectoryNotFoundException();
            }

            if (Directory.Exists(physical))
            {
                throw new FtpPathUnavailableException();
            }

            EnsureNoReparsePoints(parent, user.HomeDirectory);
            var pathLock = AcquirePathLock(physical);
            var staging = Path.Combine(_stagingDirectory, StagingPrefix + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    65536, FileOptions.SequentialScan);
                return new LocalUploadTransaction(this, physical, staging, stream, pathLock);
            }
            catch
            {
                pathLock.Release();
                throw;
            }
        }

        public void DeleteFile(FtpUserAccount user, string virtualPath)
        {
            Require(user, FtpPermission.Delete);
            if (IsRoot(user, virtualPath))
            {
                throw new UnauthorizedAccessException();
            }

            var physical = Resolve(user, virtualPath, true);
            WithPathLock(physical, () => File.Delete(physical));
        }

        public void CreateDirectory(FtpUserAccount user, string virtualPath)
        {
            Require(user, FtpPermission.Write);
            if (IsRoot(user, virtualPath))
            {
                throw new FtpPathUnavailableException();
            }

            var physical = Resolve(user, virtualPath, false);
            EnsureNoReparsePoints(Path.GetDirectoryName(physical), user.HomeDirectory);
            var parent = Path.GetDirectoryName(physical);
            if (!Directory.Exists(parent))
            {
                throw new DirectoryNotFoundException();
            }

            WithPathLock(physical, () =>
            {
                if (File.Exists(physical) || Directory.Exists(physical))
                {
                    throw new FtpPathUnavailableException();
                }

                Directory.CreateDirectory(physical);
            });
        }

        public void DeleteDirectory(FtpUserAccount user, string virtualPath)
        {
            Require(user, FtpPermission.Delete);
            if (IsRoot(user, virtualPath))
            {
                throw new UnauthorizedAccessException();
            }

            var physical = Resolve(user, virtualPath, true);
            WithPathLock(physical, () => Directory.Delete(physical, false));
        }

        public void Move(FtpUserAccount user, string sourceVirtualPath, string destinationVirtualPath)
        {
            Require(user, FtpPermission.Rename);
            if (IsRoot(user, sourceVirtualPath) || IsRoot(user, destinationVirtualPath))
            {
                throw new UnauthorizedAccessException();
            }

            var source = Resolve(user, sourceVirtualPath, true);
            var destination = Resolve(user, destinationVirtualPath, false);
            EnsureNoReparsePoints(Path.GetDirectoryName(destination), user.HomeDirectory);
            WithPathLock(source, () => WithPathLock(destination, () =>
            {
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    throw new FtpPathUnavailableException();
                }

                if (File.Exists(source))
                {
                    File.Move(source, destination);
                }
                else
                {
                    Directory.Move(source, destination);
                }
            }));
        }

        private string Resolve(FtpUserAccount user, string virtualPath, bool requireExisting)
        {
            var canonical = NormalizePath(user, "/", virtualPath);
            var root = TrimEndingSeparator(Path.GetFullPath(user.HomeDirectory));
            var physical = canonical == "/" ? root : Path.GetFullPath(Path.Combine(root,
                canonical.Substring(1).Replace('/', Path.DirectorySeparatorChar)));
            if (!IsSameOrChild(root, physical))
            {
                throw new UnauthorizedAccessException();
            }

            EnsureNoReparsePoints(physical, root);
            if (requireExisting && !File.Exists(physical) && !Directory.Exists(physical))
            {
                throw new FileNotFoundException();
            }

            return physical;
        }

        private static void AddSegments(List<string> result, string path)
        {
            foreach (var segment in path.Split('/'))
            {
                if (segment.Length == 0 || segment == ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    if (result.Count == 0)
                    {
                        throw new UnauthorizedAccessException();
                    }

                    result.RemoveAt(result.Count - 1);
                    continue;
                }

                if (segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal) ||
                    IsReservedDeviceName(segment))
                {
                    throw new InvalidDataException("The virtual path contains an invalid Windows name.");
                }

                result.Add(segment);
            }
        }

        private static bool IsReservedDeviceName(string segment)
        {
            var name = segment.Split('.')[0].TrimEnd(' ');
            return name.Equals("CON", PathComparison) || name.Equals("PRN", PathComparison) ||
                name.Equals("AUX", PathComparison) || name.Equals("NUL", PathComparison) ||
                (name.Length == 4 && (name.StartsWith("COM", PathComparison) || name.StartsWith("LPT", PathComparison)) &&
                    name[3] >= '1' && name[3] <= '9');
        }

        private static void EnsureNoReparsePoints(string path, string root)
        {
            var fullPath = Path.GetFullPath(path);
            root = TrimEndingSeparator(Path.GetFullPath(root));
            if (!IsSameOrChild(root, fullPath))
            {
                throw new UnauthorizedAccessException();
            }

            var relative = fullPath.Length == root.Length ? string.Empty : fullPath.Substring(root.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = root;
            if (IsReparsePoint(current))
            {
                throw new UnauthorizedAccessException();
            }

            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (segment.Length == 0)
                {
                    continue;
                }

                current = Path.Combine(current, segment);
                if (File.Exists(current) || Directory.Exists(current))
                {
                    if (IsReparsePoint(current))
                    {
                        throw new UnauthorizedAccessException();
                    }
                }
                else
                {
                    break;
                }
            }
        }

        private static bool IsReparsePoint(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }

        private SemaphoreSlim AcquirePathLock(string path)
        {
            var pathLock = PathLockFor(path);
            if (!pathLock.Wait(0))
            {
                throw new FtpFileSystemConflictException();
            }

            return pathLock;
        }

        private SemaphoreSlim PathLockFor(string path)
        {
            return _pathLocks.GetOrAdd(path, ignored => new SemaphoreSlim(1, 1));
        }

        private void WithPathLock(string path, Action action)
        {
            var pathLock = _pathLocks.GetOrAdd(path, ignored => new SemaphoreSlim(1, 1));
            if (!pathLock.Wait(0))
            {
                throw new FtpFileSystemConflictException();
            }

            try
            {
                action();
            }
            finally
            {
                pathLock.Release();
            }
        }

        private static void Require(FtpUserAccount user, FtpPermission permission)
        {
            if (user == null || !user.HasPermission(permission))
            {
                throw new UnauthorizedAccessException();
            }
        }

        private bool IsRoot(FtpUserAccount user, string virtualPath)
        {
            return string.Equals(NormalizePath(user, "/", virtualPath), "/", StringComparison.Ordinal);
        }

        private static bool PathsOverlap(string first, string second)
        {
            first = TrimEndingSeparator(Path.GetFullPath(first));
            second = TrimEndingSeparator(Path.GetFullPath(second));
            return IsSameOrChild(first, second) || IsSameOrChild(second, first);
        }

        private static bool IsSameOrChild(string parent, string candidate)
        {
            if (string.Equals(parent, candidate, PathComparison))
            {
                return true;
            }

            var separator = parent.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                parent.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? string.Empty
                : Path.DirectorySeparatorChar.ToString();
            return candidate.StartsWith(parent + separator, PathComparison);
        }

        private static string TrimEndingSeparator(string path)
        {
            var root = Path.GetPathRoot(path);
            while (path.Length > root.Length &&
                (path[path.Length - 1] == Path.DirectorySeparatorChar || path[path.Length - 1] == Path.AltDirectorySeparatorChar))
            {
                path = path.Substring(0, path.Length - 1);
            }

            return path;
        }

        private sealed class LocalUploadTransaction : IFtpUploadTransaction
        {
            private readonly LocalFtpFileSystem _owner;
            private readonly string _destination;
            private readonly string _staging;
            private readonly SemaphoreSlim _pathLock;
            private bool _committed;
            private bool _disposed;

            public LocalUploadTransaction(LocalFtpFileSystem owner, string destination, string staging,
                Stream stream, SemaphoreSlim pathLock)
            {
                _owner = owner;
                _destination = destination;
                _staging = staging;
                Stream = stream;
                _pathLock = pathLock;
            }

            public Stream Stream { get; private set; }

            public void Commit()
            {
                if (_disposed || _committed)
                {
                    throw new InvalidOperationException();
                }

                Stream.Flush();
                Stream.Dispose();
                var parentLock = _owner.AcquirePathLock(Path.GetDirectoryName(_destination));
                try
                {
                    EnsureNoReparsePoints(Path.GetDirectoryName(_destination), Path.GetPathRoot(_destination));
                    if (File.Exists(_destination))
                    {
                        if ((File.GetAttributes(_destination) & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new UnauthorizedAccessException();
                        }

                        File.Replace(_staging, _destination, null);
                    }
                    else
                    {
                        File.Move(_staging, _destination);
                    }

                    _committed = true;
                }
                finally
                {
                    parentLock.Release();
                }
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Stream.Dispose();
                if (!_committed && File.Exists(_staging))
                {
                    File.Delete(_staging);
                }

                _pathLock.Release();
            }
        }
    }
}