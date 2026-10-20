using System;
using System.Net;

namespace Simple.FtpServer
{
    public sealed class FtpServerOptions
    {
        public IPAddress ControlBindAddress { get; set; } = IPAddress.Loopback;
        public int ControlPort { get; set; } = 2121;
        public IPAddress PassiveBindAddress { get; set; }
        public IPAddress PassiveAdvertisedAddress { get; set; }
        public int PassivePortStart { get; set; } = 50000;
        public int PassivePortEnd { get; set; } = 50049;
        public int MaximumControlSessions { get; set; } = 20;
        public TimeSpan ControlIdleTimeout { get; set; } = TimeSpan.FromSeconds(300);
        public TimeSpan DataConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan TransferInactivityTimeout { get; set; } = TimeSpan.FromSeconds(60);
        public TimeSpan PassiveListenerLifetime { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan ShutdownLimit { get; set; } = TimeSpan.FromSeconds(10);
        public int TransferBufferSize { get; set; } = 65536;
        public string UsersFilePath { get; set; }
        public string StagingDirectoryPath { get; set; }
        public bool AllowNonLoopbackPlainFtp { get; set; }

        public IPAddress EffectivePassiveBindAddress
        {
            get { return PassiveBindAddress ?? ControlBindAddress; }
        }

        public IPAddress EffectivePassiveAdvertisedAddress
        {
            get { return PassiveAdvertisedAddress ?? ControlBindAddress; }
        }

        public void Validate()
        {
            ValidateIpv4(ControlBindAddress, nameof(ControlBindAddress));
            ValidateIpv4(EffectivePassiveBindAddress, nameof(PassiveBindAddress));
            ValidateIpv4(EffectivePassiveAdvertisedAddress, nameof(PassiveAdvertisedAddress));
            ValidatePort(ControlPort, nameof(ControlPort));
            ValidatePort(PassivePortStart, nameof(PassivePortStart));
            ValidatePort(PassivePortEnd, nameof(PassivePortEnd));
            if (PassivePortStart > PassivePortEnd)
            {
                throw new ArgumentException("The passive port range is invalid.");
            }

            if (ControlPort >= PassivePortStart && ControlPort <= PassivePortEnd &&
                AddressesMayOverlap(ControlBindAddress, EffectivePassiveBindAddress))
            {
                throw new ArgumentException("The control port overlaps the passive port range.");
            }

            if (MaximumControlSessions <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaximumControlSessions));
            }

            ValidateTimeout(ControlIdleTimeout, nameof(ControlIdleTimeout));
            ValidateTimeout(DataConnectTimeout, nameof(DataConnectTimeout));
            ValidateTimeout(TransferInactivityTimeout, nameof(TransferInactivityTimeout));
            ValidateTimeout(PassiveListenerLifetime, nameof(PassiveListenerLifetime));
            ValidateTimeout(ShutdownLimit, nameof(ShutdownLimit));
            if (ShutdownLimit > TimeSpan.FromSeconds(10))
            {
                throw new ArgumentOutOfRangeException(nameof(ShutdownLimit));
            }

            if (TransferBufferSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(TransferBufferSize));
            }

            ValidateAbsolutePath(UsersFilePath, nameof(UsersFilePath));
            ValidateAbsolutePath(StagingDirectoryPath, nameof(StagingDirectoryPath));

            if ((!IsLoopback(ControlBindAddress) || !IsLoopback(EffectivePassiveBindAddress)) &&
                !AllowNonLoopbackPlainFtp)
            {
                throw new ArgumentException("Non-loopback plain FTP bindings require explicit acknowledgment.");
            }
        }

        private static void ValidateIpv4(IPAddress address, string name)
        {
            if (address == null || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                throw new ArgumentException("An IPv4 address is required.", name);
            }
        }

        private static void ValidatePort(int port, string name)
        {
            if (port < 1 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(name);
            }
        }

        private static void ValidateTimeout(TimeSpan timeout, string name)
        {
            if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(name);
            }
        }

        private static void ValidateAbsolutePath(string path, string name)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path))
            {
                throw new ArgumentException("An absolute path is required.", name);
            }
        }

        private static bool AddressesMayOverlap(IPAddress first, IPAddress second)
        {
            return first.Equals(second) || first.Equals(IPAddress.Any) || second.Equals(IPAddress.Any);
        }

        private static bool IsLoopback(IPAddress address)
        {
            return IPAddress.IsLoopback(address);
        }
    }
}