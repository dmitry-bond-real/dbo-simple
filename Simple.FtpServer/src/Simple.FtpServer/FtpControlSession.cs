using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Simple.FtpServer
{
    internal sealed class FtpControlSession
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly HashSet<string> Commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "USER", "PASS", "QUIT", "SYST", "FEAT", "NOOP", "PWD", "CWD", "CDUP", "TYPE",
            "PASV", "EPSV", "PORT", "EPRT", "LIST", "NLST", "RETR", "STOR", "DELE", "MKD",
            "RMD", "RNFR", "RNTO", "SIZE", "MDTM", "OPTS", "AUTH", "REST", "APPE", "MLSD", "SITE", "ABOR"
        };

        private readonly string _id;
        private readonly TcpClient _client;
        private readonly FtpServerOptions _options;
        private readonly IFtpUserStore _userStore;
        private readonly IFtpFileSystem _fileSystem;
        private readonly IFtpLogSink _logSink;
        private readonly Action _closed;
        private readonly CancellationTokenSource _cancellation;
        private readonly ControlLineReader _lineReader = new ControlLineReader();
        private FtpDataEndpoint _dataEndpoint;
        private TcpClient _activeDataClient;
        private FtpUserAccount _user;
        private string _username;
        private string _workingDirectory = "/";
        private string _renameSource;
        private bool _ascii;
        private bool _epsvAll;
        private int _failedPassAttempts;
        private string _remoteEndpoint;

        public FtpControlSession(string id, TcpClient client, FtpServerOptions options,
            IFtpUserStore userStore, IFtpFileSystem fileSystem, IFtpLogSink logSink,
            CancellationToken serverCancellation, Action closed)
        {
            _id = id;
            _client = client;
            _options = options;
            _userStore = userStore;
            _fileSystem = fileSystem;
            _logSink = logSink;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
            _closed = closed;
            _remoteEndpoint = client.Client.RemoteEndPoint == null ? string.Empty :
                client.Client.RemoteEndPoint.ToString();
        }

        public Task Completion { get; private set; } = Task.CompletedTask;

        public void Start()
        {
            Completion = RunAsync();
        }

        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            CloseEndpoint();
            try
            {
                _activeDataClient?.Close();
                _client.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task RunAsync()
        {
            try
            {
                await ReplyAsync(220, "Simple FTP server ready.", CancellationToken.None).ConfigureAwait(false);
                while (!_cancellation.IsCancellationRequested)
                {
                    string line;
                    try
                    {
                        using (var idle = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token))
                        {
                            idle.CancelAfter(_options.ControlIdleTimeout);
                            line = await _lineReader.ReadLineAsync(_client.GetStream(), idle.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (!_cancellation.IsCancellationRequested)
                        {
                            await ReplyAsync(421, "Control connection timed out.", CancellationToken.None).ConfigureAwait(false);
                        }

                        break;
                    }
                    catch (FtpCommandTooLongException)
                    {
                        await ReplyAsync(500, "Command line too long.", CancellationToken.None).ConfigureAwait(false);
                        break;
                    }
                    catch (FtpProtocolException)
                    {
                        await ReplyAsync(501, "Invalid command framing.", CancellationToken.None).ConfigureAwait(false);
                        break;
                    }

                    if (line == null)
                    {
                        break;
                    }

                    if (!await ProcessCommandAsync(line).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                if (!_cancellation.IsCancellationRequested)
                {
                    Log("session-error", null, Sanitize(exception), null, null);
                }
            }
            finally
            {
                _cancellation.Cancel();
                CloseEndpoint();
                try
                {
                    _activeDataClient?.Close();
                    _client.Close();
                }
                catch (ObjectDisposedException)
                {
                }

                _cancellation.Dispose();
                _closed();
            }
        }

        private async Task MonitorControlClosureAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (_client.Client.Poll(100000, SelectMode.SelectRead) && _client.Client.Available == 0)
                    {
                        Cancel();
                        return;
                    }

                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
                Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task<bool> ProcessCommandAsync(string line)
        {
            if (line.IndexOf('\0') >= 0)
            {
                await ReplyAsync(501, "Invalid command syntax.", _cancellation.Token).ConfigureAwait(false);
                return true;
            }

            var separator = line.IndexOf(' ');
            var command = (separator < 0 ? line : line.Substring(0, separator)).ToUpperInvariant();
            var argument = separator < 0 ? string.Empty : line.Substring(separator + 1);
            if (command.Length == 0 || command.Any(character => !((character >= 'A' && character <= 'Z') ||
                (character >= '0' && character <= '9'))))
            {
                await ReplyAsync(501, "Invalid command syntax.", _cancellation.Token).ConfigureAwait(false);
                return true;
            }

            if (_renameSource != null && command != "RNTO")
            {
                _renameSource = null;
            }

            Log("command", null, command, null, null);
            if (!Commands.Contains(command))
            {
                await ReplyAsync(502, "Command not implemented.", _cancellation.Token).ConfigureAwait(false);
                return true;
            }

            if (_user == null && command != "USER" && command != "PASS" && command != "QUIT" &&
                command != "FEAT" && command != "SYST" && command != "NOOP")
            {
                await ReplyAsync(530, "Please log in with USER and PASS.", _cancellation.Token).ConfigureAwait(false);
                return true;
            }

            switch (command)
            {
                case "USER": return await UserAsync(argument).ConfigureAwait(false);
                case "PASS": return await PassAsync(argument).ConfigureAwait(false);
                case "QUIT":
                    CloseEndpoint();
                    _renameSource = null;
                    await ReplyAsync(221, "Goodbye.", _cancellation.Token).ConfigureAwait(false);
                    return false;
                case "SYST": return await SimpleCommandAsync(argument, 215, "UNIX Type: L8").ConfigureAwait(false);
                case "FEAT": return await FeaturesAsync(argument).ConfigureAwait(false);
                case "NOOP": return await ReplySimpleAsync(200, "OK.").ConfigureAwait(false);
                case "PWD": return await PrintWorkingDirectoryAsync(argument).ConfigureAwait(false);
                case "CWD": return await ChangeWorkingDirectoryAsync(argument).ConfigureAwait(false);
                case "CDUP": return await ChangeToParentAsync(argument).ConfigureAwait(false);
                case "TYPE": return await TypeAsync(argument).ConfigureAwait(false);
                case "PASV": return await PassiveAsync(argument, false).ConfigureAwait(false);
                case "EPSV": return await PassiveAsync(argument, true).ConfigureAwait(false);
                case "PORT": return await PortAsync(argument).ConfigureAwait(false);
                case "EPRT": return await EprtAsync(argument).ConfigureAwait(false);
                case "LIST": return await ListAsync(argument, false).ConfigureAwait(false);
                case "NLST": return await ListAsync(argument, true).ConfigureAwait(false);
                case "RETR": return await RetrieveAsync(argument).ConfigureAwait(false);
                case "STOR": return await StoreAsync(argument).ConfigureAwait(false);
                case "DELE": return await DeleteFileAsync(argument).ConfigureAwait(false);
                case "MKD": return await MakeDirectoryAsync(argument).ConfigureAwait(false);
                case "RMD": return await RemoveDirectoryAsync(argument).ConfigureAwait(false);
                case "RNFR": return await RenameFromAsync(argument).ConfigureAwait(false);
                case "RNTO": return await RenameToAsync(argument).ConfigureAwait(false);
                case "SIZE": return await SizeAsync(argument).ConfigureAwait(false);
                case "MDTM": return await ModificationTimeAsync(argument).ConfigureAwait(false);
                case "OPTS": return await OptionsAsync(argument).ConfigureAwait(false);
                default:
                    await ReplyAsync(502, "Command not implemented.", _cancellation.Token).ConfigureAwait(false);
                    return true;
            }
        }

        private async Task<bool> UserAsync(string username)
        {
            if (username.Length == 0 || username.Length > 64 || username.Any(character => !((character >= 'A' && character <= 'Z') ||
                (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') ||
                character == '_' || character == '-' || character == '.')))
            {
                await ReplyAsync(501, "Invalid username syntax.", _cancellation.Token).ConfigureAwait(false);
                return true;
            }

            CloseEndpoint();
            _renameSource = null;
            _user = null;
            _username = username;
            _workingDirectory = "/";
            _ascii = false;
            _epsvAll = false;
            await ReplyAsync(331, "Password required.", _cancellation.Token).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> PassAsync(string password)
        {
            if (_username == null || _user != null)
            {
                await ReplyAsync(503, "Bad sequence of commands.", _cancellation.Token).ConfigureAwait(false);
                return true;
            }

            try
            {
                var authenticated = await _userStore.AuthenticateAsync(_username, password, _cancellation.Token)
                    .ConfigureAwait(false);
                if (authenticated != null)
                {
                    _user = authenticated;
                    _workingDirectory = "/";
                    Log("authentication", null, "success", null, null);
                    await ReplyAsync(230, "Login successful.", _cancellation.Token).ConfigureAwait(false);
                    return true;
                }
            }
            catch (FtpAuthenticationBusyException)
            {
                await ReplyAsync(421, "Service temporarily unavailable.", CancellationToken.None).ConfigureAwait(false);
                return false;
            }
            catch (Exception)
            {
            }

            _failedPassAttempts++;
            Log("authentication", null, "failure", null, null);
            await ReplyAsync(530, "Login incorrect.", _cancellation.Token).ConfigureAwait(false);
            return _failedPassAttempts < 5;
        }

        private async Task<bool> FeaturesAsync(string argument)
        {
            if (argument.Length != 0)
            {
                return await ReplySimpleAsync(501, "Invalid command syntax.").ConfigureAwait(false);
            }

            await WriteControlAsync("211-Features\r\n EPSV\r\n EPRT\r\n SIZE\r\n MDTM\r\n UTF8\r\n211 End\r\n",
                _cancellation.Token).ConfigureAwait(false);
            Log("reply", null, "FEAT", null, null, 211);
            return true;
        }

        private async Task<bool> SimpleCommandAsync(string argument, int code, string message)
        {
            if (argument.Length != 0)
            {
                return await ReplySimpleAsync(501, "Invalid command syntax.").ConfigureAwait(false);
            }

            return await ReplySimpleAsync(code, message).ConfigureAwait(false);
        }

        private async Task<bool> PrintWorkingDirectoryAsync(string argument)
        {
            if (argument.Length != 0)
            {
                return await ReplySimpleAsync(501, "Invalid command syntax.").ConfigureAwait(false);
            }

            return await ReplySimpleAsync(257, "\"" + _workingDirectory.Replace("\"", "\"\"") + "\" is the current directory.")
                .ConfigureAwait(false);
        }

        private async Task<bool> ChangeWorkingDirectoryAsync(string argument)
        {
            if (argument.Length == 0)
            {
                return await ReplySimpleAsync(501, "A directory path is required.").ConfigureAwait(false);
            }

            try
            {
                RequirePermission(FtpPermission.List);
                var path = _fileSystem.NormalizePath(_user, _workingDirectory, argument);
                Log("filesystem-command", path, "CWD", null, null, null);
                if (!_fileSystem.DirectoryExists(_user, path))
                {
                    return await ReplySimpleAsync(550, "Directory unavailable.").ConfigureAwait(false);
                }

                _workingDirectory = path;
                return await ReplySimpleAsync(250, "Directory changed.").ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return await ReplyFileSystemErrorAsync(exception).ConfigureAwait(false);
            }
        }

        private async Task<bool> ChangeToParentAsync(string argument)
        {
            if (argument.Length != 0)
            {
                return await ReplySimpleAsync(501, "Invalid command syntax.").ConfigureAwait(false);
            }

            try
            {
                RequirePermission(FtpPermission.List);
                if (_workingDirectory != "/")
                {
                    var separator = _workingDirectory.LastIndexOf('/');
                    _workingDirectory = separator <= 0 ? "/" : _workingDirectory.Substring(0, separator);
                }

                return await ReplySimpleAsync(250, "Directory changed.").ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return await ReplyFileSystemErrorAsync(exception).ConfigureAwait(false);
            }
        }

        private async Task<bool> TypeAsync(string argument)
        {
            if (string.Equals(argument, "I", StringComparison.OrdinalIgnoreCase))
            {
                _ascii = false;
                return await ReplySimpleAsync(200, "Type set to I.").ConfigureAwait(false);
            }

            if (string.Equals(argument, "A", StringComparison.OrdinalIgnoreCase))
            {
                _ascii = true;
                return await ReplySimpleAsync(200, "Type set to A.").ConfigureAwait(false);
            }

            return await ReplySimpleAsync(argument.Length == 0 ? 501 : 504,
                argument.Length == 0 ? "Invalid command syntax." : "Transfer type not supported.").ConfigureAwait(false);
        }

        private async Task<bool> PassiveAsync(string argument, bool extended)
        {
            if ((!extended && argument.Length != 0) || (extended && argument.Length != 0 &&
                !string.Equals(argument, "ALL", StringComparison.OrdinalIgnoreCase) && argument != "1" && argument != "2"))
            {
                return await ReplySimpleAsync(501, "Invalid command syntax.").ConfigureAwait(false);
            }

            if (extended && string.Equals(argument, "ALL", StringComparison.OrdinalIgnoreCase))
            {
                CloseEndpoint();
                _epsvAll = true;
                return await ReplySimpleAsync(200, "EPSV ALL accepted.").ConfigureAwait(false);
            }

            if (extended && argument == "2")
            {
                return await ReplySimpleAsync(522, "Network protocol not supported.").ConfigureAwait(false);
            }

            if (_epsvAll && !extended)
            {
                return await ReplySimpleAsync(501, "EPSV ALL is in effect.").ConfigureAwait(false);
            }

            CloseEndpoint();
            FtpPassiveEndpoint passive;
            var bindAddress = _options.EffectivePassiveBindAddress;
            var found = false;
            for (var port = _options.PassivePortStart; port <= _options.PassivePortEnd; port++)
            {
                try
                {
                    passive = new FtpPassiveEndpoint(bindAddress, port, _options.PassiveListenerLifetime);
                    SetEndpoint(passive);
                    found = true;
                    if (extended)
                    {
                        return await ReplySimpleAsync(229, "Entering Extended Passive Mode (|||" + port + "|).")
                            .ConfigureAwait(false);
                    }

                    var advertised = _options.PassiveAdvertisedAddress;
                    if (advertised == null)
                    {
                        advertised = ((IPEndPoint)_client.Client.LocalEndPoint).Address;
                    }

                    if (advertised.Equals(IPAddress.Any))
                    {
                        advertised = ((IPEndPoint)_client.Client.LocalEndPoint).Address;
                    }

                    var bytes = advertised.GetAddressBytes();
                    var response = "Entering Passive Mode (" + string.Join(",", bytes.Select(item => item.ToString(CultureInfo.InvariantCulture))) +
                        "," + (port / 256).ToString(CultureInfo.InvariantCulture) + "," + (port % 256).ToString(CultureInfo.InvariantCulture) + ").";
                    return await ReplySimpleAsync(227, response).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                }
            }

            if (!found)
            {
                CloseEndpoint();
            }

            return await ReplySimpleAsync(425, "Cannot open passive connection.").ConfigureAwait(false);
        }

        private async Task<bool> PortAsync(string argument)
        {
            if (_epsvAll)
            {
                return await ReplySimpleAsync(501, "EPSV ALL is in effect.").ConfigureAwait(false);
            }

            var parts = argument.Split(',');
            if (parts.Length != 6)
            {
                return await ReplySimpleAsync(501, "Invalid PORT syntax.").ConfigureAwait(false);
            }

            var values = new int[6];
            for (var index = 0; index < parts.Length; index++)
            {
                if (parts[index].Length == 0 || parts[index].Any(character => character < '0' || character > '9') ||
                    !int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out values[index]))
                {
                    return await ReplySimpleAsync(501, "Invalid PORT syntax.").ConfigureAwait(false);
                }
            }

            if (values.Any(value => value > 255))
            {
                return await ReplySimpleAsync(501, "Invalid PORT address.").ConfigureAwait(false);
            }

            var port = values[4] * 256 + values[5];
            var address = new IPAddress(values.Take(4).Select(value => (byte)value).ToArray());
            if (!IsAllowedActiveEndpoint(address, port))
            {
                return await ReplySimpleAsync(501, "Invalid PORT endpoint.").ConfigureAwait(false);
            }

            SetEndpoint(new FtpActiveEndpoint(address, port));
            return await ReplySimpleAsync(200, "PORT command successful.").ConfigureAwait(false);
        }

        private async Task<bool> EprtAsync(string argument)
        {
            if (_epsvAll)
            {
                return await ReplySimpleAsync(501, "EPSV ALL is in effect.").ConfigureAwait(false);
            }

            if (argument.Length < 7 || argument[0] < 33 || argument[0] > 126 || char.IsLetterOrDigit(argument[0]))
            {
                return await ReplySimpleAsync(501, "Invalid EPRT syntax.").ConfigureAwait(false);
            }

            var fields = argument.Split(argument[0]);
            if (fields.Length != 5 || fields[0].Length != 0 || fields[4].Length != 0)
            {
                return await ReplySimpleAsync(501, "Invalid EPRT syntax.").ConfigureAwait(false);
            }

            if (fields[1] != "1")
            {
                return await ReplySimpleAsync(522, "Network protocol not supported.").ConfigureAwait(false);
            }

            IPAddress address;
            int port;
            if (!TryParseIpv4(fields[2], out address) ||
                fields[3].Length == 0 || fields[3].Any(character => character < '0' || character > '9') ||
                !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
                !IsAllowedActiveEndpoint(address, port))
            {
                return await ReplySimpleAsync(501, "Invalid EPRT endpoint.").ConfigureAwait(false);
            }

            SetEndpoint(new FtpActiveEndpoint(address, port));
            return await ReplySimpleAsync(200, "EPRT command successful.").ConfigureAwait(false);
        }

        private bool IsAllowedActiveEndpoint(IPAddress address, int port)
        {
            var remote = _client.Client.RemoteEndPoint as IPEndPoint;
            return remote != null && address.Equals(remote.Address) && port >= 1024 && port <= 65535;
        }

        private static bool TryParseIpv4(string value, out IPAddress address)
        {
            address = null;
            var parts = value.Split('.');
            if (parts.Length != 4)
            {
                return false;
            }

            var octets = new byte[4];
            for (var index = 0; index < parts.Length; index++)
            {
                int octet;
                if (parts[index].Length == 0 || parts[index].Any(character => character < '0' || character > '9') ||
                    !int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out octet) ||
                    octet > 255)
                {
                    return false;
                }

                octets[index] = (byte)octet;
            }

            address = new IPAddress(octets);
            return true;
        }

        private void SetEndpoint(FtpDataEndpoint endpoint)
        {
            CloseEndpoint();
            _dataEndpoint = endpoint;
        }

        private void CloseEndpoint()
        {
            var previous = _dataEndpoint;
            _dataEndpoint = null;
            previous?.Dispose();
        }

        private async Task<bool> ListAsync(string argument, bool namesOnly)
        {
            string pathArgument;
            var showAll = false;
            if (!ParseListingArgument(argument, out pathArgument, out showAll))
            {
                return await ReplySimpleAsync(501, "Unsupported listing option.").ConfigureAwait(false);
            }

            var path = _workingDirectory;
            if (pathArgument.Length != 0)
            {
                try
                {
                    path = _fileSystem.NormalizePath(_user, _workingDirectory, pathArgument);
                }
                catch (Exception exception)
                {
                    CloseEndpoint();
                    return await ReplyFileSystemErrorAsync(exception).ConfigureAwait(false);
                }
            }

            IReadOnlyList<FtpFileEntry> entries;
            try
            {
                RequirePermission(FtpPermission.List);
                Log("filesystem-command", path, namesOnly ? "NLST" : "LIST", null, null, null);
                entries = _fileSystem.GetEntries(_user, path);
            }
            catch (Exception exception)
            {
                CloseEndpoint();
                return await ReplyFileSystemErrorAsync(exception).ConfigureAwait(false);
            }

            var listing = new StringBuilder();
            foreach (var entry in entries)
            {
                var line = namesOnly ? entry.Name : FormatListing(entry);
                listing.Append(line).Append("\r\n");
            }

            var bytes = Utf8.GetBytes(listing.ToString());
            return await TransferAsync(path, async stream =>
            {
                await WriteNetworkWithTimeoutAsync(stream, bytes, 0, bytes.Length, _cancellation.Token).ConfigureAwait(false);
                return (long)bytes.Length;
            }).ConfigureAwait(false);
        }

        private static bool ParseListingArgument(string argument, out string path, out bool showAll)
        {
            path = argument;
            showAll = false;
            if (argument.Length == 0 || argument[0] != '-')
            {
                return true;
            }

            var separator = argument.IndexOf(' ');
            var option = separator < 0 ? argument : argument.Substring(0, separator);
            if (option != "-a" && option != "-l")
            {
                return false;
            }

            showAll = option == "-a";
            path = separator < 0 ? string.Empty : argument.Substring(separator + 1);
            return true;
        }

        private static string FormatListing(FtpFileEntry entry)
        {
            var type = entry.IsDirectory ? 'd' : '-';
            var date = entry.LastWriteTimeUtc.ToUniversalTime();
            var yearOrTime = date.Year == DateTime.UtcNow.Year
                ? date.ToString("HH:mm", CultureInfo.InvariantCulture)
                : date.ToString("yyyy", CultureInfo.InvariantCulture);
            return type + "rw-r--r-- 1 ftp ftp " + entry.Length.ToString(CultureInfo.InvariantCulture) + " " +
                date.ToString("MMM", CultureInfo.InvariantCulture) + " " + date.Day.ToString(CultureInfo.InvariantCulture) + " " +
                yearOrTime + " " + entry.Name;
        }

        private async Task<bool> RetrieveAsync(string argument)
        {
            if (argument.Length == 0)
            {
                CloseEndpoint();
                return await ReplySimpleAsync(501, "A file path is required.").ConfigureAwait(false);
            }

            string path;
            try
            {
                RequirePermission(FtpPermission.Read);
                path = _fileSystem.NormalizePath(_user, _workingDirectory, argument);
            }
            catch (Exception exception)
            {
                CloseEndpoint();
                return await ReplyFileSystemErrorAsync(exception).ConfigureAwait(false);
            }

            Log("filesystem-command", path, "RETR", null, null, null);
            return await TransferAsync(path, async stream =>
            {
                using (var file = _fileSystem.OpenRead(_user, path))
                {
                    return await CopyToDataAsync(file, stream, _ascii, _cancellation.Token).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }

        private async Task<bool> StoreAsync(string argument)
        {
            if (argument.Length == 0)
            {
                CloseEndpoint();
                return await ReplySimpleAsync(501, "A file path is required.").ConfigureAwait(false);
            }

            string path;
            try
            {
                RequirePermission(FtpPermission.Write);
                path = _fileSystem.NormalizePath(_user, _workingDirectory, argument);
            }
            catch (Exception exception)
            {
                CloseEndpoint();
                return await ReplyFileSystemErrorAsync(exception).ConfigureAwait(false);
            }

            Log("filesystem-command", path, "STOR", null, null, null);
            return await TransferAsync(path, async stream =>
            {
                using (var upload = _fileSystem.BeginUpload(_user, path))
                {
                    var transferred = await CopyFromDataAsync(stream, upload.Stream, _ascii, _cancellation.Token)
                        .ConfigureAwait(false);
                    stream.Dispose();
                    upload.Commit();
                    return transferred;
                }
            }).ConfigureAwait(false);
        }

        private async Task<bool> TransferAsync(string virtualPath, Func<Stream, Task<long>> transfer)
        {
            var endpoint = _dataEndpoint;
            _dataEndpoint = null;
            if (endpoint == null || !endpoint.BeginTransfer())
            {
                endpoint?.Dispose();
                return await ReplySimpleAsync(425, "Use PORT, PASV, EPRT, or EPSV first.").ConfigureAwait(false);
            }

            var stopwatch = Stopwatch.StartNew();
            TcpClient dataClient = null;
            var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
            var controlMonitor = MonitorControlClosureAsync(monitorCancellation.Token);
            try
            {
                await ReplyAsync(150, "Opening data connection.", _cancellation.Token).ConfigureAwait(false);
                try
                {
                    dataClient = await endpoint.ConnectAsync(_client.Client.RemoteEndPoint as IPEndPoint,
                        _options.DataConnectTimeout, _cancellation.Token).ConfigureAwait(false);
                }
                catch (Exception) when (!_cancellation.IsCancellationRequested)
                {
                    await ReplyAsync(425, "Cannot establish data connection.", _cancellation.Token).ConfigureAwait(false);
                    Log("transfer", virtualPath, "connection-failed", null, stopwatch.Elapsed, null);
                    return true;
                }

                _activeDataClient = dataClient;
                try
                {
                    long transferred;
                    using (dataClient)
                    using (var stream = dataClient.GetStream())
                    {
                        transferred = await transfer(stream).ConfigureAwait(false);
                    }

                    dataClient = null;
                    _activeDataClient = null;
                    stopwatch.Stop();
                    Log("transfer", virtualPath, "completed", transferred, stopwatch.Elapsed, null);
                    await ReplyAsync(226, "Transfer complete.", _cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_cancellation.IsCancellationRequested)
                {
                    await ReplyAsync(426, "Transfer timed out.", CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    var code = exception is UnauthorizedAccessException || exception is FileNotFoundException ||
                        exception is DirectoryNotFoundException || exception is InvalidDataException ||
                        exception is FtpPathUnavailableException ? 550 :
                        exception is FtpFileSystemConflictException ? 450 :
                        exception is FtpDataTransferException ? 426 : 451;
                    await ReplyAsync(code, code == 550 ? "File unavailable." : code == 450 ? "Path is busy." :
                        code == 426 ? "Transfer interrupted." : "Local processing error.", CancellationToken.None)
                        .ConfigureAwait(false);
                    Log("transfer", virtualPath, "failed", null, stopwatch.Elapsed, null);
                }

                return true;
            }
            finally
            {
                monitorCancellation.Cancel();
                try
                {
                    await controlMonitor.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    monitorCancellation.Dispose();
                }

                if (dataClient != null)
                {
                    dataClient.Close();
                }

                _activeDataClient = null;
                endpoint.Dispose();
            }
        }

        private async Task<long> CopyToDataAsync(Stream source, Stream destination, bool ascii, CancellationToken cancellationToken)
        {
            var buffer = new byte[_options.TransferBufferSize];
            var converter = ascii ? new AsciiTransferConverter(false) : null;
            long transferred = 0;
            while (true)
            {
                var count = await ReadWithTimeoutAsync(source, buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    if (converter != null)
                    {
                        var tail = converter.Convert(buffer, 0, 0, true);
                        if (tail.Length != 0)
                        {
                                await WriteNetworkWithTimeoutAsync(destination, tail, 0, tail.Length, cancellationToken)
                                .ConfigureAwait(false);
                                transferred += tail.Length;
                        }
                    }
                            return transferred;
                }

                var output = converter == null ? CopyBuffer(buffer, count) : converter.Convert(buffer, 0, count, false);
                if (output.Length != 0)
                {
                    await WriteNetworkWithTimeoutAsync(destination, output, 0, output.Length, cancellationToken)
                        .ConfigureAwait(false);
                    transferred += output.Length;
                }
            }
        }

        private async Task<long> CopyFromDataAsync(Stream source, Stream destination, bool ascii, CancellationToken cancellationToken)
        {
            var buffer = new byte[_options.TransferBufferSize];
            var converter = ascii ? new AsciiTransferConverter(true) : null;
            long transferred = 0;
            while (true)
            {
                var count = await ReadNetworkWithTimeoutAsync(source, buffer, cancellationToken).ConfigureAwait(false);
                var output = converter == null
                    ? (count == 0 ? new byte[0] : CopyBuffer(buffer, count))
                    : converter.Convert(buffer, 0, count, count == 0);
                if (output.Length != 0)
                {
                    await destination.WriteAsync(output, 0, output.Length, cancellationToken).ConfigureAwait(false);
                }

                transferred += count;
                if (count == 0)
                {
                    return transferred;
                }
            }
        }

        private async Task<int> ReadWithTimeoutAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(_options.TransferInactivityTimeout);
                try
                {
                    return await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Transfer inactivity timeout.");
                }
            }
        }

        private async Task WriteWithTimeoutAsync(Stream stream, byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(_options.TransferInactivityTimeout);
                try
                {
                    await stream.WriteAsync(buffer, offset, count, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Transfer inactivity timeout.");
                }
            }
        }

        private async Task<int> ReadNetworkWithTimeoutAsync(Stream stream, byte[] buffer,
            CancellationToken cancellationToken)
        {
            try
            {
                return await ReadWithTimeoutAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new FtpDataTransferException(exception);
            }
        }

        private async Task WriteNetworkWithTimeoutAsync(Stream stream, byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            try
            {
                await WriteWithTimeoutAsync(stream, buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new FtpDataTransferException(exception);
            }
        }

        private static byte[] CopyBuffer(byte[] buffer, int count)
        {
            var output = new byte[count];
            Buffer.BlockCopy(buffer, 0, output, 0, count);
            return output;
        }

        private async Task<bool> DeleteFileAsync(string argument)
        {
            return await FileMutationAsync(argument, "DELE", FtpPermission.Delete,
                path => _fileSystem.DeleteFile(_user, path), 250, "File deleted.").ConfigureAwait(false);
        }

        private async Task<bool> MakeDirectoryAsync(string argument)
        {
            if (argument.Length == 0)
            {
                return await ReplySimpleAsync(501, "A directory path is required.").ConfigureAwait(false);
            }

            try
            {
                RequirePermission(FtpPermission.Write);
                var path = _fileSystem.NormalizePath(_user, _workingDirectory, argument);
                Log("filesystem-command", path, "MKD", null, null, null);
                _fileSystem.CreateDirectory(_user, path);
                return await ReplySimpleAsync(257, "\"" + path.Replace("\"", "\"\"") + "\" created.").ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return await ReplyFileSystemErrorAsync(exception).ConfigureAwait(false);
            }
        }

        private async Task<bool> RemoveDirectoryAsync(string argument)
        {
            return await FileMutationAsync(argument, "RMD", FtpPermission.Delete,
                path => _fileSystem.DeleteDirectory(_user, path), 250, "Directory removed.").ConfigureAwait(false);
        }

        private async Task<bool> FileMutationAsync(string argument, string command, FtpPermission permission,
            Action<string> mutation, int successCode, string successText)
        {
            if (argument.Length == 0)
            {
                return await ReplySimpleAsync(501, "A path is required.").ConfigureAwait(false);
            }

            try
            {
                RequirePermission(permission);
                var path = _fileSystem.NormalizePath(_user, _workingDirectory, argument);
                Log("filesystem-command", path, command, null, null, null);
                mutation(path);
                return await ReplySimpleAsync(successCode, successText).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return await ReplyFileSystemErrorAsync(exception).ConfigureAwait(false);
            }
        }

        private async Task<bool> RenameFromAsync(string argument)
        {
            if (argument.Length == 0)
            {
                return await ReplySimpleAsync(501, "A source path is required.").ConfigureAwait(false);
            }

            try
            {
                RequirePermission(FtpPermission.Rename);
                var path = _fileSystem.NormalizePath(_user, _workingDirectory, argument);
                Log("filesystem-command", path, "RNFR", null, null, null);
                if (!_fileSystem.FileExists(_user, path) && !_fileSystem.DirectoryExists(_user, path))
                {
                    return await ReplySimpleAsync(550, "File unavailable.").ConfigureAwait(false);
                }

                _renameSource = path;
                return await ReplySimpleAsync(350, "Ready for destination name.").ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return await ReplyFileSystemErrorAsync(exception).ConfigureAwait(false);
            }
        }

        private async Task<bool> RenameToAsync(string argument)
        {
            var source = _renameSource;
            _renameSource = null;
            if (source == null)
            {
                return await ReplySimpleAsync(503, "RNFR required before RNTO.").ConfigureAwait(false);
            }

            if (argument.Length == 0)
            {
                return await ReplySimpleAsync(501, "A destination path is required.").ConfigureAwait(false);
            }

            try
            {
                RequirePermission(FtpPermission.Rename);
                var destination = _fileSystem.NormalizePath(_user, _workingDirectory, argument);
                Log("filesystem-command", destination, "RNTO", null, null, null);
                _fileSystem.Move(_user, source, destination);
                return await ReplySimpleAsync(250, "Rename successful.").ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return await ReplyFileSystemErrorAsync(exception).ConfigureAwait(false);
            }
        }

        private async Task<bool> SizeAsync(string argument)
        {
            if (argument.Length == 0)
            {
                return await ReplySimpleAsync(501, "A file path is required.").ConfigureAwait(false);
            }

            try
            {
                RequirePermission(FtpPermission.Read);
                if (_ascii)
                {
                    return await ReplySimpleAsync(504, "SIZE requires binary transfer type.").ConfigureAwait(false);
                }

                var path = _fileSystem.NormalizePath(_user, _workingDirectory, argument);
                Log("filesystem-command", path, "SIZE", null, null, null);
                var length = _fileSystem.GetLength(_user, path);
                return await ReplySimpleAsync(213, length.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return await ReplyFileSystemErrorAsync(exception).ConfigureAwait(false);
            }
        }

        private async Task<bool> ModificationTimeAsync(string argument)
        {
            if (argument.Length == 0)
            {
                return await ReplySimpleAsync(501, "A file path is required.").ConfigureAwait(false);
            }

            try
            {
                RequirePermission(FtpPermission.Read);
                var path = _fileSystem.NormalizePath(_user, _workingDirectory, argument);
                Log("filesystem-command", path, "MDTM", null, null, null);
                var modified = _fileSystem.GetLastWriteTimeUtc(_user, path);
                return await ReplySimpleAsync(213, modified.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return await ReplyFileSystemErrorAsync(exception).ConfigureAwait(false);
            }
        }

        private async Task<bool> OptionsAsync(string argument)
        {
            if (string.Equals(argument, "UTF8 ON", StringComparison.OrdinalIgnoreCase))
            {
                return await ReplySimpleAsync(200, "UTF8 enabled.").ConfigureAwait(false);
            }

            return await ReplySimpleAsync(501, "Unsupported OPTS argument.").ConfigureAwait(false);
        }

        private async Task<bool> ReplyFileSystemErrorAsync(Exception exception)
        {
            if (exception is FtpFileSystemConflictException)
            {
                return await ReplySimpleAsync(450, "Requested path is busy.").ConfigureAwait(false);
            }

            if (exception is UnauthorizedAccessException || exception is FileNotFoundException ||
                exception is DirectoryNotFoundException || exception is InvalidDataException ||
                exception is FtpPathUnavailableException)
            {
                return await ReplySimpleAsync(550, "File unavailable.").ConfigureAwait(false);
            }

            Log("filesystem-error", null, Sanitize(exception), null, null);
            return await ReplySimpleAsync(451, "Local processing error.").ConfigureAwait(false);
        }

        private void RequirePermission(FtpPermission permission)
        {
            if (_user == null || !_user.HasPermission(permission))
            {
                throw new UnauthorizedAccessException();
            }
        }

        private async Task<bool> ReplySimpleAsync(int code, string message)
        {
            await ReplyAsync(code, message, _cancellation.Token).ConfigureAwait(false);
            return true;
        }

        private async Task ReplyAsync(int code, string message, CancellationToken cancellationToken)
        {
            if (message.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            {
                message = "Operation completed.";
            }

            await WriteControlAsync(code.ToString(CultureInfo.InvariantCulture) + " " + message + "\r\n",
                cancellationToken).ConfigureAwait(false);
            Log("reply", null, message, null, null, code);
        }

        private async Task WriteControlAsync(string response, CancellationToken cancellationToken)
        {
            var bytes = Utf8.GetBytes(response);
            await _client.GetStream().WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        }

        private void Log(string eventName, string virtualPath, string detail, long? bytes,
            TimeSpan? duration = null, int? replyCode = null)
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
                    SessionId = _id,
                    RemoteEndpoint = _remoteEndpoint,
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

        private static string Sanitize(Exception exception)
        {
            return exception.GetType().Name;
        }
    }

    internal sealed class FtpDataTransferException : Exception
    {
        public FtpDataTransferException(Exception innerException) : base("The data connection failed.", innerException)
        {
        }
    }

    internal abstract class FtpDataEndpoint : IDisposable
    {
        public abstract bool BeginTransfer();
        public abstract Task<TcpClient> ConnectAsync(IPEndPoint controlPeer, TimeSpan timeout,
            CancellationToken cancellationToken);
        public abstract void Dispose();
    }

    internal sealed class FtpActiveEndpoint : FtpDataEndpoint
    {
        private readonly IPAddress _address;
        private readonly int _port;
        private bool _used;

        public FtpActiveEndpoint(IPAddress address, int port)
        {
            _address = address;
            _port = port;
        }

        public override bool BeginTransfer()
        {
            if (_used)
            {
                return false;
            }

            _used = true;
            return true;
        }

        public override async Task<TcpClient> ConnectAsync(IPEndPoint controlPeer, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                var connect = client.ConnectAsync(_address, _port);
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var delay = Task.Delay(timeout, deadline.Token);
                    if (await Task.WhenAny(connect, delay).ConfigureAwait(false) != connect)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException();
                    }

                    deadline.Cancel();
                }

                await connect.ConfigureAwait(false);
                return client;
            }
            catch
            {
                client.Close();
                throw;
            }
        }

        public override void Dispose()
        {
        }
    }

    internal sealed class FtpPassiveEndpoint : FtpDataEndpoint
    {
        private readonly TcpListener _listener;
        private readonly Timer _lifetimeTimer;
        private bool _disposed;
        private bool _transferStarted;

        public FtpPassiveEndpoint(IPAddress address, int port, TimeSpan listenerLifetime)
        {
            _listener = new TcpListener(address, port);
            _listener.Server.ExclusiveAddressUse = true;
            _listener.Start(1);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _lifetimeTimer = new Timer(Expire, null, listenerLifetime, Timeout.InfiniteTimeSpan);
        }

        public int Port { get; private set; }

        public override bool BeginTransfer()
        {
            if (_disposed || _transferStarted)
            {
                return false;
            }

            _transferStarted = true;
            _lifetimeTimer.Change(Timeout.Infinite, Timeout.Infinite);
            return true;
        }

        public override async Task<TcpClient> ConnectAsync(IPEndPoint controlPeer, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException();
                }

                var accept = _listener.AcceptTcpClientAsync();
                var delay = Task.Delay(remaining, cancellationToken);
                if (await Task.WhenAny(accept, delay).ConfigureAwait(false) != accept)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException();
                }

                var client = await accept.ConfigureAwait(false);
                var remote = client.Client.RemoteEndPoint as IPEndPoint;
                if (remote != null && controlPeer != null && remote.Address.Equals(controlPeer.Address))
                {
                    return client;
                }

                client.Close();
            }
        }

        public override void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetimeTimer.Dispose();
            _listener.Stop();
        }

        private void Expire(object state)
        {
            Dispose();
        }
    }

    internal sealed class AsciiTransferConverter
    {
        private readonly bool _upload;
        private bool _pendingCarriageReturn;

        public AsciiTransferConverter(bool upload)
        {
            _upload = upload;
        }

        public byte[] Convert(byte[] source, int offset, int count, bool final)
        {
            var output = new byte[count * 2 + 2];
            var outputOffset = 0;
            for (var index = offset; index < offset + count; index++)
            {
                var value = source[index];
                if (_pendingCarriageReturn)
                {
                    if (value == (byte)'\n')
                    {
                        output[outputOffset++] = (byte)'\r';
                        output[outputOffset++] = (byte)'\n';
                        _pendingCarriageReturn = false;
                        continue;
                    }

                    if (_upload && value == 0)
                    {
                        output[outputOffset++] = (byte)'\r';
                        _pendingCarriageReturn = false;
                        continue;
                    }

                    output[outputOffset++] = (byte)'\r';
                    if (!_upload)
                    {
                        output[outputOffset++] = 0;
                    }

                    _pendingCarriageReturn = false;
                }

                if (value == (byte)'\r')
                {
                    _pendingCarriageReturn = true;
                }
                else
                {
                    output[outputOffset++] = value;
                }
            }

            if (final && _pendingCarriageReturn)
            {
                output[outputOffset++] = (byte)'\r';
                if (!_upload)
                {
                    output[outputOffset++] = 0;
                }

                _pendingCarriageReturn = false;
            }

            if (outputOffset == output.Length)
            {
                return output;
            }

            var trimmed = new byte[outputOffset];
            Buffer.BlockCopy(output, 0, trimmed, 0, outputOffset);
            return trimmed;
        }
    }
}