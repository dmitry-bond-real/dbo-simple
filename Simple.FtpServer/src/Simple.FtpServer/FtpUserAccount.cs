using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Simple.FtpServer
{
    public sealed class FtpUserAccount
    {
        private readonly ReadOnlyCollection<FtpPermission> _permissions;

        internal FtpUserAccount(string username, bool enabled, string homeDirectory,
            IEnumerable<FtpPermission> permissions, int passwordVersion, string passwordAlgorithm,
            int passwordIterations, byte[] passwordSalt, byte[] passwordHash)
        {
            Username = username;
            Enabled = enabled;
            HomeDirectory = homeDirectory;
            _permissions = Array.AsReadOnly(new List<FtpPermission>(permissions).ToArray());
            PasswordVersion = passwordVersion;
            PasswordAlgorithm = passwordAlgorithm;
            PasswordIterations = passwordIterations;
            _passwordSalt = (byte[])passwordSalt.Clone();
            _passwordHash = (byte[])passwordHash.Clone();
        }

        public string Username { get; private set; }
        public bool Enabled { get; private set; }
        public string HomeDirectory { get; private set; }
        public IReadOnlyList<FtpPermission> Permissions { get { return _permissions; } }
        internal int PasswordVersion { get; private set; }
        internal string PasswordAlgorithm { get; private set; }
        internal int PasswordIterations { get; private set; }
        internal byte[] PasswordSalt { get { return (byte[])_passwordSalt.Clone(); } }
        internal byte[] PasswordHash { get { return (byte[])_passwordHash.Clone(); } }

        private readonly byte[] _passwordSalt;
        private readonly byte[] _passwordHash;

        public bool HasPermission(FtpPermission permission)
        {
            return _permissions.Contains(permission);
        }
    }
}