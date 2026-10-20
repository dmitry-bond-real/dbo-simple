using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Simple.FtpServer
{
    public sealed class IniFtpUserStore : IFtpUserStore
    {
        private const int CurrentPasswordVersion = 1;
        private const string CurrentPasswordAlgorithm = "PBKDF2-HMAC-SHA256";
        private const int DefaultIterations = 600000;
        private const int DerivedKeyLength = 32;
        private const int SaltLength = 16;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly SemaphoreSlim DerivationSlots = new SemaphoreSlim(4, 4);

        private readonly string _filePath;
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        private readonly SecurityIdentifier[] _authorizedOperators;
        private volatile Dictionary<string, FtpUserAccount> _users =
            new Dictionary<string, FtpUserAccount>(StringComparer.OrdinalIgnoreCase);

        public IniFtpUserStore(string filePath) : this(filePath, null)
        {
        }

        public IniFtpUserStore(string filePath, IEnumerable<SecurityIdentifier> authorizedOperators)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath))
            {
                throw new ArgumentException("An absolute users file path is required.", nameof(filePath));
            }

            _filePath = Path.GetFullPath(filePath);
            _authorizedOperators = authorizedOperators == null ? new SecurityIdentifier[0] : authorizedOperators.ToArray();
        }

        public IReadOnlyCollection<FtpUserAccount> Users
        {
            get { return Array.AsReadOnly(_users.Values.ToArray()); }
        }

        public async Task ReloadAsync(CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                SecureStoreLocation();
                var parsed = await Task.Run(() => ParseDatabase(_filePath), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _users = parsed;
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public async Task<FtpUserAccount> AuthenticateAsync(string username, string password,
            CancellationToken cancellationToken)
        {
            if (!DerivationSlots.Wait(0))
            {
                throw new FtpAuthenticationBusyException();
            }

            try
            {
                FtpUserAccount account = null;
                var found = username != null && _users.TryGetValue(username, out account);
                if (!found || !account.Enabled)
                {
                    var dummySalt = new byte[SaltLength];
                    using (var random = RandomNumberGenerator.Create())
                    {
                        random.GetBytes(dummySalt);
                    }

                    DeriveKey(password ?? string.Empty, dummySalt, DefaultIterations);
                    return null;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var candidate = DeriveKey(password ?? string.Empty, account.PasswordSalt, account.PasswordIterations);
                return FixedTimeEquals(candidate, account.PasswordHash) ? account : null;
            }
            finally
            {
                DerivationSlots.Release();
            }
        }

        public Task CreateUserAsync(string username, string password, string homeDirectory,
            IEnumerable<FtpPermission> permissions, CancellationToken cancellationToken)
        {
            ValidateUsername(username);
            ValidatePassword(password);
            var normalizedHome = ValidateHome(homeDirectory);
            var permissionSet = ValidatePermissions(permissions);
            var record = CreatePasswordRecord(password);
            return MutateAsync(users =>
            {
                if (users.ContainsKey(username))
                {
                    throw new InvalidOperationException("The username already exists.");
                }

                EnsureHomeDoesNotOverlap(users.Values, normalizedHome, null);
                users.Add(username, new FtpUserAccount(username, true, normalizedHome, permissionSet,
                    CurrentPasswordVersion, CurrentPasswordAlgorithm, DefaultIterations, record.Item1, record.Item2));
            }, cancellationToken);
        }

        public Task UpdateUserAsync(string username, bool enabled, string homeDirectory,
            IEnumerable<FtpPermission> permissions, CancellationToken cancellationToken)
        {
            ValidateUsername(username);
            var normalizedHome = ValidateHome(homeDirectory);
            var permissionSet = ValidatePermissions(permissions);
            return MutateAsync(users =>
            {
                var existing = GetUser(users, username);
                users[existing.Username] = Copy(existing, enabled, normalizedHome,
                    permissionSet, existing.PasswordSalt, existing.PasswordHash,
                    existing.PasswordIterations);
            }, cancellationToken);
        }

        public Task SetEnabledAsync(string username, bool enabled, CancellationToken cancellationToken)
        {
            return MutateAsync(users =>
            {
                var existing = GetUser(users, username);
                users[existing.Username] = Copy(existing, enabled, existing.HomeDirectory,
                    existing.Permissions, existing.PasswordSalt, existing.PasswordHash,
                    existing.PasswordIterations);
            }, cancellationToken);
        }

        public Task ChangePasswordAsync(string username, string newPassword, CancellationToken cancellationToken)
        {
            ValidatePassword(newPassword);
            var record = CreatePasswordRecord(newPassword);
            return MutateAsync(users =>
            {
                var existing = GetUser(users, username);
                users[existing.Username] = Copy(existing, existing.Enabled, existing.HomeDirectory,
                    existing.Permissions, record.Item1, record.Item2, DefaultIterations);
            }, cancellationToken);
        }

        private async Task MutateAsync(Action<Dictionary<string, FtpUserAccount>> change,
            CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            string temporaryPath = null;
            try
            {
                var changed = new Dictionary<string, FtpUserAccount>(_users, StringComparer.OrdinalIgnoreCase);
                change(changed);
                EnsureHomesValid(changed.Values);
                var contents = Serialize(changed.Values);
                var directory = Path.GetDirectoryName(_filePath);
                FtpStorageSecurity.EnsureNoReparsePoints(directory);
                Directory.CreateDirectory(directory);
                FtpStorageSecurity.SecureDirectory(directory, _authorizedOperators);
                if (File.Exists(_filePath))
                {
                    FtpStorageSecurity.SecureFile(_filePath, _authorizedOperators);
                }

                temporaryPath = Path.Combine(directory, "." + Path.GetFileName(_filePath) + "." +
                    Guid.NewGuid().ToString("N") + ".tmp");
                var encodedContents = StrictUtf8.GetBytes(contents);
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(encodedContents, 0, encodedContents.Length, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(_filePath))
                {
                    File.Replace(temporaryPath, _filePath, null);
                }
                else
                {
                    File.Move(temporaryPath, _filePath);
                }

                FtpStorageSecurity.SecureFile(_filePath, _authorizedOperators);
                temporaryPath = null;
                _users = changed;
            }
            finally
            {
                if (temporaryPath != null && File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }

                _writeGate.Release();
            }
        }

        private void SecureStoreLocation()
        {
            var directory = Path.GetDirectoryName(_filePath);
            FtpStorageSecurity.EnsureNoReparsePoints(directory);
            Directory.CreateDirectory(directory);
            FtpStorageSecurity.SecureDirectory(directory, _authorizedOperators);
            if (File.Exists(_filePath))
            {
                FtpStorageSecurity.SecureFile(_filePath, _authorizedOperators);
            }
        }

        private static Dictionary<string, FtpUserAccount> ParseDatabase(string path)
        {
            if (!File.Exists(path))
            {
                throw new InvalidDataException("The users database does not exist.");
            }

            var text = File.ReadAllText(path, StrictUtf8);
            var records = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> current = null;
            var lineNumber = 0;
            foreach (var rawLine in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            {
                lineNumber++;
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    var section = line.Substring(1, line.Length - 2);
                    if (!section.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
                    {
                        throw InvalidIni(lineNumber);
                    }

                    var username = section.Substring(5);
                    ValidateUsername(username);
                    if (records.ContainsKey(username))
                    {
                        throw InvalidIni(lineNumber);
                    }

                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    records.Add(username, current);
                    continue;
                }

                var separator = rawLine.IndexOf('=');
                if (current == null || separator <= 0)
                {
                    throw InvalidIni(lineNumber);
                }

                var key = rawLine.Substring(0, separator).Trim();
                var value = rawLine.Substring(separator + 1).Trim();
                if (key.Length == 0 || current.ContainsKey(key))
                {
                    throw InvalidIni(lineNumber);
                }

                current.Add(key, value);
            }

            var users = new Dictionary<string, FtpUserAccount>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in records)
            {
                users.Add(pair.Key, ParseAccount(pair.Key, pair.Value));
            }

            EnsureHomesValid(users.Values);
            return users;
        }

        private static FtpUserAccount ParseAccount(string username, Dictionary<string, string> values)
        {
            var expected = new HashSet<string>(new[]
            {
                "Enabled", "HomeDirectory", "PasswordVersion", "PasswordAlgorithm", "PasswordIterations",
                "PasswordSalt", "PasswordHash", "Permissions"
            }, StringComparer.OrdinalIgnoreCase);
            if (values.Keys.Any(key => !expected.Contains(key)) || expected.Any(key => !values.ContainsKey(key)))
            {
                throw new InvalidDataException("The users database contains an invalid account record.");
            }

            bool enabled;
            int version;
            int iterations;
            if (!bool.TryParse(values["Enabled"], out enabled) ||
                !int.TryParse(values["PasswordVersion"], NumberStyles.None, CultureInfo.InvariantCulture, out version) ||
                !int.TryParse(values["PasswordIterations"], NumberStyles.None, CultureInfo.InvariantCulture, out iterations))
            {
                throw new InvalidDataException("The users database contains an invalid account record.");
            }

            if (version != CurrentPasswordVersion ||
                !string.Equals(values["PasswordAlgorithm"], CurrentPasswordAlgorithm, StringComparison.Ordinal) ||
                iterations < 100000 || iterations > 2000000)
            {
                throw new InvalidDataException("The users database contains an unsupported password record.");
            }

            byte[] salt;
            byte[] hash;
            try
            {
                salt = Convert.FromBase64String(values["PasswordSalt"]);
                hash = Convert.FromBase64String(values["PasswordHash"]);
            }
            catch (FormatException)
            {
                throw new InvalidDataException("The users database contains an invalid password record.");
            }

            if (salt.Length < SaltLength || hash.Length != DerivedKeyLength)
            {
                throw new InvalidDataException("The users database contains an invalid password record.");
            }

            var home = ValidateHome(values["HomeDirectory"]);
            var permissions = ParsePermissions(values["Permissions"]);
            return new FtpUserAccount(username, enabled, home, permissions, version,
                values["PasswordAlgorithm"], iterations, salt, hash);
        }

        private static FtpUserAccount GetUser(Dictionary<string, FtpUserAccount> users, string username)
        {
            FtpUserAccount account;
            if (username == null || !users.TryGetValue(username, out account))
            {
                throw new InvalidOperationException("The username does not exist.");
            }

            return account;
        }

        private static FtpUserAccount Copy(FtpUserAccount source, bool enabled, string home,
            IEnumerable<FtpPermission> permissions, byte[] salt, byte[] hash, int iterations)
        {
            return new FtpUserAccount(source.Username, enabled, home, permissions, CurrentPasswordVersion,
                CurrentPasswordAlgorithm, iterations, salt, hash);
        }

        private static Tuple<byte[], byte[]> CreatePasswordRecord(string password)
        {
            var salt = new byte[SaltLength];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(salt);
            }

            DerivationSlots.Wait();
            try
            {
                return Tuple.Create(salt, DeriveKey(password, salt, DefaultIterations));
            }
            finally
            {
                DerivationSlots.Release();
            }
        }

        private static byte[] DeriveKey(string password, byte[] salt, int iterations)
        {
            using (var derive = new Rfc2898DeriveBytes(StrictUtf8.GetBytes(password), salt, iterations,
                HashAlgorithmName.SHA256))
            {
                return derive.GetBytes(DerivedKeyLength);
            }
        }

        private static bool FixedTimeEquals(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length)
            {
                return false;
            }

            var difference = 0;
            for (var index = 0; index < first.Length; index++)
            {
                difference |= first[index] ^ second[index];
            }

            return difference == 0;
        }

        private static void ValidateUsername(string username)
        {
            if (string.IsNullOrEmpty(username) || username.Length > 64 ||
                username.Any(character => !((character >= 'A' && character <= 'Z') ||
                    (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') ||
                    character == '_' || character == '-' || character == '.')))
            {
                throw new ArgumentException("The username is invalid.", nameof(username));
            }
        }

        private static void ValidatePassword(string password)
        {
            if (password == null || password.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            {
                throw new ArgumentException("The password is invalid.", nameof(password));
            }

            int characterCount;
            try
            {
                characterCount = new StringInfo(password).LengthInTextElements;
                StrictUtf8.GetBytes(password);
            }
            catch (EncoderFallbackException)
            {
                throw new ArgumentException("The password contains invalid Unicode.", nameof(password));
            }

            if (characterCount < 6 || characterCount > 256)
            {
                throw new ArgumentException("The password length is invalid.", nameof(password));
            }
        }

        private static string ValidateHome(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            {
                throw new InvalidDataException("An account home must be an absolute path.");
            }

            var fullPath = TrimEndingSeparator(Path.GetFullPath(path));
            if (!Directory.Exists(fullPath))
            {
                throw new InvalidDataException("An account home directory does not exist.");
            }

            EnsureNoReparsePoints(fullPath);
            return fullPath;
        }

        private static void EnsureNoReparsePoints(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            var current = root;
            var remainder = fullPath.Substring(root.Length);
            foreach (var segment in remainder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (segment.Length == 0)
                {
                    continue;
                }

                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Account homes cannot contain reparse points.");
                }
            }
        }

        private static void EnsureHomesValid(IEnumerable<FtpUserAccount> users)
        {
            var accounts = users.ToArray();
            foreach (var account in accounts)
            {
                ValidateHome(account.HomeDirectory);
            }

            for (var first = 0; first < accounts.Length; first++)
            {
                for (var second = first + 1; second < accounts.Length; second++)
                {
                    if (PathsOverlap(accounts[first].HomeDirectory, accounts[second].HomeDirectory))
                    {
                        throw new InvalidDataException("Account home directories cannot overlap.");
                    }
                }
            }
        }

        private static void EnsureHomeDoesNotOverlap(IEnumerable<FtpUserAccount> users, string home, string exceptUsername)
        {
            foreach (var account in users)
            {
                if (string.Equals(account.Username, exceptUsername, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (PathsOverlap(home, account.HomeDirectory))
                {
                    throw new InvalidDataException("Account home directories cannot overlap.");
                }
            }
        }

        private static bool PathsOverlap(string first, string second)
        {
            first = TrimEndingSeparator(Path.GetFullPath(first));
            second = TrimEndingSeparator(Path.GetFullPath(second));
            return IsSameOrChild(first, second) || IsSameOrChild(second, first);
        }

        private static bool IsSameOrChild(string parent, string candidate)
        {
            if (string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var separator = parent.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                parent.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? string.Empty
                : Path.DirectorySeparatorChar.ToString();
            return candidate.StartsWith(parent + separator, StringComparison.OrdinalIgnoreCase);
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

        private static FtpPermission[] ValidatePermissions(IEnumerable<FtpPermission> permissions)
        {
            if (permissions == null)
            {
                throw new ArgumentNullException(nameof(permissions));
            }

            var result = permissions.Distinct().ToArray();
            if (result.Any(permission => !Enum.IsDefined(typeof(FtpPermission), permission)))
            {
                throw new ArgumentException("The permission set is invalid.", nameof(permissions));
            }

            return result;
        }

        private static FtpPermission[] ParsePermissions(string value)
        {
            if (value.Length == 0)
            {
                return new FtpPermission[0];
            }

            var values = value.Split(',');
            var result = new List<FtpPermission>();
            foreach (var item in values)
            {
                FtpPermission permission;
                if (!Enum.TryParse(item.Trim(), true, out permission) ||
                    !Enum.IsDefined(typeof(FtpPermission), permission) || result.Contains(permission))
                {
                    throw new InvalidDataException("The users database contains an invalid permission set.");
                }

                result.Add(permission);
            }

            return result.ToArray();
        }

        private static string Serialize(IEnumerable<FtpUserAccount> users)
        {
            var builder = new StringBuilder();
            foreach (var user in users.OrderBy(item => item.Username, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("[user:").Append(user.Username).AppendLine("]");
                builder.Append("Enabled=").AppendLine(user.Enabled.ToString().ToLowerInvariant());
                builder.Append("HomeDirectory=").AppendLine(user.HomeDirectory);
                builder.Append("PasswordVersion=").AppendLine(user.PasswordVersion.ToString(CultureInfo.InvariantCulture));
                builder.Append("PasswordAlgorithm=").AppendLine(user.PasswordAlgorithm);
                builder.Append("PasswordIterations=").AppendLine(user.PasswordIterations.ToString(CultureInfo.InvariantCulture));
                builder.Append("PasswordSalt=").AppendLine(Convert.ToBase64String(user.PasswordSalt));
                builder.Append("PasswordHash=").AppendLine(Convert.ToBase64String(user.PasswordHash));
                builder.Append("Permissions=").AppendLine(string.Join(",", user.Permissions.Select(item => item.ToString())));
                builder.AppendLine();
            }

            return builder.ToString();
        }

        private static InvalidDataException InvalidIni(int lineNumber)
        {
            return new InvalidDataException("The users database has invalid INI syntax at line " +
                lineNumber.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }

    public sealed class FtpAuthenticationBusyException : Exception
    {
        public FtpAuthenticationBusyException() : base("Authentication capacity is temporarily exhausted.")
        {
        }
    }
}