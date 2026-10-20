/*
 * Simple FTP Server - written by AI (GPT-6.1-sol) by Dmitry Bond's request.
 * Then used Claude Sonnet 5 to polish functionality.
 * 
 * CLI examples:
 *   Simple.FtpServer.Host.exe user --users C:\tools\ftpSrv\ftpSrv.ini add uzver C:\inetpub\myFtp *
 *   Simple.FtpServer.Host.exe serve --users C:\tools\ftpSrv\ftpSrv.ini --staging C:\inetpub\myFtp\staging 
 *   Simple.FtpServer.Host.exe serve --users C:\tools\ftpSrv\ftpSrv.ini --staging C:\inetpub\myFtp\staging --bind 192.168.173.25 --allow-nonloopback 
 *   Simple.FtpServer.Host.exe serve --users C:\tools\ftpSrv\ftpSrv.ini --staging C:\inetpub\myFtp\staging --bind myPcNetworkName --allow-nonloopback 
 *   Simple.FtpServer.Host.exe nics-list 
 */


using Simple.FtpServer;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Simple.FtpServer.Host
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            StreamWriter logWriter = null;
            try
            {
                args = SetupLogging(args, out logWriter);
                PrintCaption();
                try
                {
                    return RunAsync(args).GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(exception.Message);
                    return 1;
                }
            }
            finally
            {
                if (logWriter != null)
                {
                    Console.Out.Flush();
                    Console.Error.Flush();
                    logWriter.Dispose();
                }
            }
        }

        /// <summary>
        /// Looks for a "--log &lt;path&gt;" option (or a "CLI:log" appSettings default) and, when
        /// present, duplicates all subsequent Console.Out/Console.Error output to that file.
        /// Returns the arguments with the "--log" option removed, since it is a global option
        /// and not understood by the "serve"/"user" sub-commands.
        /// </summary>
        private static string[] SetupLogging(string[] args, out StreamWriter logWriter)
        {
            logWriter = null;
            string logPath = null;
            var remaining = new List<string>(args);

            for (var index = 0; index < remaining.Count; index++)
            {
                if (string.Equals(remaining[index], "--log", StringComparison.OrdinalIgnoreCase))
                {
                    if (index + 1 >= remaining.Count)
                    {
                        throw new ArgumentException("Missing value for option: --log");
                    }

                    logPath = remaining[index + 1];
                    remaining.RemoveRange(index, 2);
                    break;
                }
            }

            if (logPath == null)
            {
                logPath = ConfigurationManager.AppSettings["CLI:log"];
            }

            if (string.IsNullOrWhiteSpace(logPath))
            {
                return remaining.ToArray();
            }

            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            logWriter = new StreamWriter(logPath, append: true, Encoding.UTF8) { AutoFlush = true };
            var teeOut = new TeeTextWriter(Console.Out, logWriter);
            Console.SetOut(teeOut);
            Console.SetError(new TeeTextWriter(Console.Error, logWriter));

            return remaining.ToArray();
        }

        private static async Task<int> RunAsync(string[] args)
        {
            if (args.Length == 0 || args[0] == "--help" || args[0] == "help")
            {
                PrintHelp();
                return 0;
            }

            if (args[0] == "serve")
            {
                return await ServeAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
            }

            if (args[0] == "user")
            {
                return await ManageUserAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
            }

            if (args[0] == "nics-list")
            {
                PrintNicsList();
                return 0;
            }

            PrintHelp();
            return 2;
        }

        private static async Task<int> ServeAsync(string[] arguments)
        {
            var flags = new[] { "--allow-nonloopback", "--enable-vpn" };
            var values = ParseOptions(arguments, flags);
            ApplyConfigDefaults(values, flags);
            
            bool isVpnEnabled = values.ContainsKey("--enable-vpn");
            if (isVpnEnabled)
            {
                VirtualAdapterKeywords[0] = "---"; // remove "vpn" as marker of 'interface virtuality'
            }

            var usersPath = Required(values, "--users");
            var stagingPath = Required(values, "--staging");
            var options = new FtpServerOptions
            {
                UsersFilePath = usersPath,
                StagingDirectoryPath = stagingPath,
                ControlBindAddress = Address(values, "--bind", IPAddress.Loopback),
                ControlPort = Integer(values, "--port", 2121),
                PassivePortStart = Integer(values, "--passive-start", 50000),
                PassivePortEnd = Integer(values, "--passive-end", 50049),
                AllowNonLoopbackPlainFtp = values.ContainsKey("--allow-nonloopback")
            };

            if (values.TryGetValue("--priority-nic", out var priorityNicName))
            {
                if (TryFindPriorityNicAddress(priorityNicName, out var priorityAddress))
                {
                    Console.WriteLine("Priority NIC \"{0}\" matched - using its address {1}.", priorityNicName, priorityAddress);
                    options.ControlBindAddress = priorityAddress;
                }
                else
                {
                    Console.WriteLine("Priority NIC \"{0}\" not found, not Up, or has no IPv4 address - ignoring.", priorityNicName);
                }
            }
            //Console.WriteLine("Choose IPv4: " + options.ControlBindAddress);
            PrintFtpOptions(options);

            if (values.ContainsKey("--passive-bind"))
            {
                options.PassiveBindAddress = IPAddress.Parse(values["--passive-bind"]);
            }

            if (values.ContainsKey("--advertise"))
            {
                options.PassiveAdvertisedAddress = IPAddress.Parse(values["--advertise"]);
            }

            var store = new IniFtpUserStore(usersPath);
            var fileSystem = new LocalFtpFileSystem(stagingPath);
            using (var server = new FtpServer(options, store, fileSystem, new ConsoleLogSink()))
            {
                await server.StartAsync(CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine("Started at: {0}", NskTimestampOf(DateTime.Now).Substring(0, 23));
                Console.WriteLine("FTP listening on {0}:{1}.", options.ControlBindAddress, options.ControlPort);
                Console.WriteLine("Press Ctrl+C to stop.");
                var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                ConsoleCancelEventHandler cancelHandler = (sender, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    stopped.TrySetResult(true);
                };
                Console.CancelKeyPress += cancelHandler;
                try
                {
                    await stopped.Task.ConfigureAwait(false);
                    await server.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    Console.CancelKeyPress -= cancelHandler;
                }
            }

            return 0;
        }

        private static async Task<int> ManageUserAsync(string[] arguments)
        {
            if (arguments.Length < 2 || arguments[0] != "--users")
            {
                throw new ArgumentException("Use: user --users <absolute-path> <add|update|enable|disable|password|list> ...");
            }

            var path = arguments[1];
            if (!Path.IsPathRooted(path))
            {
                throw new ArgumentException("The users INI path must be absolute.");
            }

            if (arguments.Length < 3)
            {
                throw new ArgumentException("An account operation is required.");
            }

            var operation = arguments[2];
            var store = new IniFtpUserStore(path);
            if (operation == "add" && !File.Exists(path))
            {
                if (arguments.Length != 6)
                {
                    throw new ArgumentException("Use: user --users <path> add <username> <home> <permissions>");
                }

                var password = ReadConfirmedPassword();
                await store.CreateUserAsync(arguments[3], password, arguments[4], ParsePermissions(arguments[5]),
                    CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine("User created.");
                return 0;
            }

            await store.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
            switch (operation)
            {
                case "add":
                    if (arguments.Length != 6)
                    {
                        throw new ArgumentException("Use: user --users <path> add <username> <home> <permissions>");
                    }

                    await store.CreateUserAsync(arguments[3], ReadConfirmedPassword(), arguments[4],
                        ParsePermissions(arguments[5]), CancellationToken.None).ConfigureAwait(false);
                    Console.WriteLine("User created.");
                    break;
                case "update":
                    if (arguments.Length != 7)
                    {
                        throw new ArgumentException("Use: user --users <path> update <username> <home> <permissions> <true|false>");
                    }

                    await store.UpdateUserAsync(arguments[3], bool.Parse(arguments[6]), arguments[4],
                        ParsePermissions(arguments[5]), CancellationToken.None).ConfigureAwait(false);
                    Console.WriteLine("User updated.");
                    break;
                case "enable":
                case "disable":
                    if (arguments.Length != 4)
                    {
                        throw new ArgumentException("Use: user --users <path> enable|disable <username>");
                    }

                    await store.SetEnabledAsync(arguments[3], operation == "enable", CancellationToken.None)
                        .ConfigureAwait(false);
                    Console.WriteLine("User status updated.");
                    break;
                case "password":
                    if (arguments.Length != 4)
                    {
                        throw new ArgumentException("Use: user --users <path> password <username>");
                    }

                    await store.ChangePasswordAsync(arguments[3], ReadConfirmedPassword(), CancellationToken.None)
                        .ConfigureAwait(false);
                    Console.WriteLine("Password changed.");
                    break;
                case "list":
                    if (arguments.Length != 3)
                    {
                        throw new ArgumentException("Use: user --users <path> list");
                    }

                    foreach (var account in store.Users.OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("{0}\t{1}\t{2}\t{3}", account.Username,
                            account.Enabled ? "enabled" : "disabled", account.HomeDirectory,
                            string.Join(",", account.Permissions.Select(permission => permission.ToString())));
                    }

                    break;
                default:
                    throw new ArgumentException("Unknown account operation.");
            }

            return 0;
        }

        private static string ReadConfirmedPassword()
        {
            Console.Write("Password: ");
            var password = ReadMaskedPassword();
            Console.Write("Confirm password: ");
            var confirmation = ReadMaskedPassword();
            if (!string.Equals(password, confirmation, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The passwords do not match.");
            }

            return password;
        }

        private static string ReadMaskedPassword()
        {
            var password = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return password.ToString();
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0)
                    {
                        password.Length--;
                    }

                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    password.Append(key.KeyChar);
                }
            }
        }

        private static FtpPermission[] ParsePermissions(string value)
        {
            if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
            {
                return new FtpPermission[0];
            }

            var permissions = new List<FtpPermission>();

            if (string.Equals(value, "*", StringComparison.OrdinalIgnoreCase))
            {
                permissions.Add(FtpPermission.List);
                permissions.Add(FtpPermission.Read);
                permissions.Add(FtpPermission.Write);
                permissions.Add(FtpPermission.Delete);
                permissions.Add(FtpPermission.Rename);
                return permissions.ToArray();
            }

            foreach (var item in value.Split(','))
            {
                FtpPermission permission;
                if (!Enum.TryParse(item.Trim(), true, out permission) ||
                    !Enum.IsDefined(typeof(FtpPermission), permission) || permissions.Contains(permission))
                {
                    throw new ArgumentException("Permissions must be a comma-separated list of List, Read, Write, Delete, Rename, or none.");
                }

                permissions.Add(permission);
            }

            return permissions.ToArray();
        }

        /// <summary>
        /// Fills in any options not already supplied on the command line with values from the
        /// &lt;appSettings&gt; section of the app.config, using keys prefixed with "CLI:".
        /// Example: &lt;add key="CLI:bind" value="192.168.1.10" /&gt; supplies a default for --bind.
        /// For boolean flags (e.g. --allow-nonloopback), the appSettings value is parsed as a
        /// boolean; only "true" activates the flag.
        /// </summary>
        private static void ApplyConfigDefaults(IDictionary<string, string> values, IEnumerable<string> flags)
        {
            var flagSet = new HashSet<string>(flags, StringComparer.OrdinalIgnoreCase);
            const string prefix = "CLI:";

            foreach (var settingKey in ConfigurationManager.AppSettings.AllKeys)
            {
                if (settingKey == null || !settingKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var optionName = "--" + settingKey.Substring(prefix.Length);
                if (values.ContainsKey(optionName))
                {
                    continue;
                }

                var settingValue = ConfigurationManager.AppSettings[settingKey];

                if (flagSet.Contains(optionName))
                {
                    if (bool.TryParse(settingValue, out var flagEnabled) && flagEnabled)
                    {
                        values.Add(optionName, string.Empty);
                    }

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(settingValue))
                {
                    values.Add(optionName, settingValue);
                }
            }
        }

        private static Dictionary<string, string> ParseOptions(string[] arguments, IEnumerable<string> flags)
        {
            var flagSet = new HashSet<string>(flags, StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < arguments.Length; index++)
            {
                var name = arguments[index];
                if (!name.StartsWith("--", StringComparison.Ordinal) || values.ContainsKey(name))
                {
                    throw new ArgumentException("Invalid or duplicate option: " + name);
                }

                if (flagSet.Contains(name))
                {
                    values.Add(name, string.Empty);
                    continue;
                }

                if (index + 1 >= arguments.Length)
                {
                    throw new ArgumentException("Missing value for option: " + name);
                }

                values.Add(name, arguments[++index]);
            }

            return values;
        }

        private static string Required(IDictionary<string, string> values, string name)
        {
            string value;
            if (!values.TryGetValue(name, out value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Required option is missing: " + name);
            }

            return value;
        }

        private static int Integer(IDictionary<string, string> values, string name, int defaultValue)
        {
            string value;
            return values.TryGetValue(name, out value)
                ? int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)
                : defaultValue;
        }

        private static IPAddress SelectBestIPv4Address(IPAddress[] candidates)
        {
            var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();

            // Map each candidate address to the NIC that owns it.
            IPAddress best = null;
            int bestScore = int.MinValue;

            foreach (var addr in candidates)
            {
                if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    continue;

                int score = 0;
                var owningNic = nics.FirstOrDefault(n =>
                    n.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(addr)));

                if (owningNic != null)
                {
                    if (owningNic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        score -= 100;

                    if (IsVirtualAdapter(owningNic))
                        score -= 50;

                    if (owningNic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                        score -= 200;

                    // Prefer real Ethernet/Wireless adapters.
                    if (owningNic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet ||
                        owningNic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
                        score += 50;
                }
                else
                {
                    // No matching NIC found (unusual) - treat as neutral.
                    score -= 10;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = addr;
                }
            }

            return best;
        }

        private static bool IsVirtualAdapter(System.Net.NetworkInformation.NetworkInterface nic)
        {
            if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                return true;

            var text = (nic.Description + " " + nic.Name).ToLowerInvariant();

            return VirtualAdapterKeywords.Any(marker => text.Contains(marker));
        }

        /// <summary>
        /// Looks for an "Up" network interface matching <paramref name="nicName"/> that has at
        /// least one bound IPv4 address. When <paramref name="nicName"/> starts and ends with
        /// "/" (e.g. "/^Ethernet.*$/"), the enclosed text is treated as a regular expression
        /// matched against the NIC name; otherwise an exact (case-insensitive) name match is used.
        /// </summary>
        private static bool TryFindPriorityNicAddress(string nicName, out IPAddress address)
        {
            address = null;

            if (string.IsNullOrWhiteSpace(nicName))
            {
                return false;
            }

            Regex regex = null;
            if (nicName.Length >= 2 && nicName.StartsWith("/", StringComparison.Ordinal) &&
                nicName.EndsWith("/", StringComparison.Ordinal))
            {
                var pattern = nicName.Substring(1, nicName.Length - 2);
                regex = new Regex(pattern, RegexOptions.IgnoreCase);
            }

            var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            foreach (var nic in nics)
            {
                var nameMatches = regex != null
                    ? regex.IsMatch(nic.Name)
                    : string.Equals(nic.Name, nicName, StringComparison.OrdinalIgnoreCase);

                if (!nameMatches)
                {
                    continue;
                }

                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    continue;
                }

                var ipv4 = nic.GetIPProperties().UnicastAddresses
                    .Select(u => u.Address)
                    .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                if (ipv4 != null)
                {
                    address = ipv4;
                    return true;
                }
            }

            return false;
        }

        private static IPAddress Address(IDictionary<string, string> values, string name, IPAddress defaultValue)
        {
            string value;
            if (values.TryGetValue(name, out value))
            {
                if (!IPAddress.TryParse(value, out var address))
                {
                    IPHostEntry iphe = Dns.GetHostEntry(value);
                    if (iphe.AddressList.Length > 0)
                    {
                        var ipv4Candidates = iphe.AddressList
                            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            .ToArray();

                        address = SelectBestIPv4Address(ipv4Candidates);

                        if (address == null)
                            throw new ArgumentException("No valid IPv4 address found for option: " + name);
                    }
                    else
                        throw new ArgumentException("Invalid IP address for option: " + name);
                }
                return address;
            }
            return defaultValue;
        }

        private static void PrintNicsList()
        {
            var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();

            foreach (var nic in nics)
            {
                var props = nic.GetIPProperties();
                var addresses = props.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ||
                                u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    .ToArray();

                Console.WriteLine("{0}  [{1}, {2}]{3}",
                    nic.Name,
                    nic.NetworkInterfaceType,
                    nic.OperationalStatus,
                    IsVirtualAdapter(nic) ? ", virtual" : string.Empty);
                Console.WriteLine("  {0}", nic.Description);

                if (addresses.Length == 0)
                {
                    Console.WriteLine("    (no IP addresses bound)");
                }
                else
                {
                    foreach (var unicast in addresses)
                    {
                        if (unicast.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            Console.WriteLine("    {0}  (mask {1})", unicast.Address, unicast.IPv4Mask);
                        }
                        else
                        {
                            Console.WriteLine("    {0}", unicast.Address);
                        }
                    }
                }

                Console.WriteLine();
            }
        }

        private static void PrintFtpOptions(FtpServerOptions pOpts)
        {
            Console.WriteLine("         AllowNonLoopbackPlainFtp: {0}", pOpts.AllowNonLoopbackPlainFtp);
            Console.WriteLine("               ControlBindAddress: {0}", pOpts.ControlBindAddress);
            Console.WriteLine("               ControlIdleTimeout: {0}", pOpts.ControlIdleTimeout);
            Console.WriteLine("                      ControlPort: {0}", pOpts.ControlPort);
            Console.WriteLine("               DataConnectTimeout: {0}", pOpts.DataConnectTimeout);
            Console.WriteLine("EffectivePassiveAdvertisedAddress: {0}", pOpts.EffectivePassiveAdvertisedAddress);
            Console.WriteLine("      EffectivePassiveBindAddress: {0}", pOpts.EffectivePassiveBindAddress);
            Console.WriteLine("           MaximumControlSessions: {0}", pOpts.MaximumControlSessions);
            Console.WriteLine("         PassiveAdvertisedAddress: {0}", pOpts.PassiveAdvertisedAddress);
            Console.WriteLine("               PassiveBindAddress: {0}", pOpts.PassiveBindAddress);
            Console.WriteLine("          PassiveListenerLifetime: {0}", pOpts.PassiveListenerLifetime);
            Console.WriteLine("                 PassivePortStart: {0}", pOpts.PassivePortStart);
            Console.WriteLine("                   PassivePortEnd: {0}", pOpts.PassivePortEnd);
            Console.WriteLine("                    ShutdownLimit: {0}", pOpts.ShutdownLimit);
            Console.WriteLine("             StagingDirectoryPath: {0}", pOpts.StagingDirectoryPath);
            Console.WriteLine("               TransferBufferSize: {0}", pOpts.TransferBufferSize);
            Console.WriteLine("        TransferInactivityTimeout: {0}", pOpts.TransferInactivityTimeout);
            Console.WriteLine("                    UsersFilePath: {0}", pOpts.UsersFilePath);
        }

        private static void PrintCaption()
        {
            string caption = string.Format("Simple.FtpServer host (.NET Framework 4.8) version {0}/{1}", ApplicationVersion, ApplicationFileVersion);
            Console.WriteLine(caption);
        }

        private static void PrintHelp()
        {
            string appName = Path.GetFileNameWithoutExtension(ActualAssembly.Location);
            string usage = string.Format("Usage: {0} command [options...]", appName);
            Console.WriteLine(usage);
            Console.WriteLine("Supported commands");
            Console.WriteLine("  serve  - start the FTP server");
            Console.WriteLine("  user  - maintain users db");
            Console.WriteLine("  nics-list  - display network interfaces and their bound IP addresses");
            Console.WriteLine("");
            Console.WriteLine("Supported options");
            Console.WriteLine("  common:");
            Console.WriteLine("    --users <absolute-path>  - path to users database");
            Console.WriteLine("    --log <path>  - duplicate all console output to the given file (any command)");
            Console.WriteLine("  command[serve]:");
            Console.WriteLine("    --users <absolute-path> --staging <absolute-path> [--bind IPv4] [--port n]");
            Console.WriteLine("      [--passive-bind IPv4] [--advertise IPv4] [--passive-start n] [--passive-end n]");
            Console.WriteLine("      [--allow-nonloopback] [--priority-nic NicName|/regex/]");
            Console.WriteLine("  command[user]:");
            Console.WriteLine("    --users <absolute-path> add <username> <home> <permissions>");
            Console.WriteLine("    --users <absolute-path> update <username> <home> <permissions> <true|false>");
            Console.WriteLine("    --users <absolute-path> enable|disable <username>");
            Console.WriteLine("    --users <absolute-path> password <username>");
            Console.WriteLine("    --users <absolute-path> list");
            Console.WriteLine("Permission names: List,Read,Write,Delete,Rename or None. Also can use '*' to specify all permissions.");
            Console.WriteLine("Note: passwords are entered at a masked prompt.");
        }

        #region Utilities 

        private static Assembly _actualAssembly = null;
        /// <summary>Determing which Assembly should be used for getting path, name, version, etc. You can assign your Assembly object to this property</summary>
        internal static Assembly ActualAssembly
        {
            get
            {
                if (_actualAssembly == null)
                    _actualAssembly = Assembly.GetEntryAssembly();
                if (_actualAssembly == null)
                    _actualAssembly = Assembly.GetCallingAssembly();
                if (_actualAssembly == null)
                    _actualAssembly = Assembly.GetExecutingAssembly();
                return _actualAssembly;
            }
            set { _actualAssembly = value; }
        }


        /// <summary>Return value specified in [AssemblyVersion] for EntryAssembly</summary>
        internal static string ApplicationVersion
        {
            get
            {
                return ActualAssembly.GetName().Version.ToString();
            }
        }
        /// <summary>Return value specified in [AssemblyFileVersion] for EntryAssembly</summary>
        internal static string ApplicationFileVersion
        {
            get
            {
                Assembly asm = ActualAssembly;
                object[] attrs = asm.GetCustomAttributes(false);
                foreach (object attr in attrs)
                {
                    if (attr is AssemblyFileVersionAttribute)
                    {
                        AssemblyFileVersionAttribute a = (AssemblyFileVersionAttribute)attr;
                        return a.Version;
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// Converts specified DateTime value into NSK-timestamp string
        /// </summary>
        /// <param name="pDT">DateTime value to convert</param>
        /// <returns>NSK-timestamp string</returns>
        internal static string NskTimestampOf(DateTime pDT)
        {
            return string.Format("{0:0###}-{1:0#}-{2:0#}:{3:0#}:{4:0#}:{5:0#}.{6:0#####}",
                pDT.Year, pDT.Month, pDT.Day, pDT.Hour, pDT.Minute, pDT.Second, pDT.Millisecond * 1000);
        }

        private static string[] VirtualAdapterKeywords =
        {
                "vpn", // this one replacable with "---" when VPN enabled
                "virtual",
                "hyper-v",
                "vmware",
                "virtualbox",
                "vbox",
                "docker",
                "wsl",
                "tap-windows",
                "tunnel",
                "loopback",
                "pseudo",
                "npcap",
                "wan miniport",
            };

        #endregion // Utilities 

        #region Helper Classes

        private sealed class ConsoleLogSink : IFtpLogSink
        {
            public static long SESSION_NO = 1;
            public Dictionary<string, long> SessionsList = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            public void Write(FtpLogEntry entry)
            {
                string sId = entry.SessionId ?? "-";
                if (!string.IsNullOrEmpty(entry.SessionId))
                {
                    long sNum = -1;
                    if (!SessionsList.TryGetValue(sId, out sNum))
                    {
                        sNum = SESSION_NO;
                        SESSION_NO++;
                        SessionsList[sId] = sNum;
                        Console.WriteLine("{0:o} added session [s#{1}] = {2}", 
                            entry.TimestampUtc, sNum, sId);
                    }
                    sId = string.Format("[s#{0}]", sNum);
                }
                Console.WriteLine("{0:o} {1} {2} {3} {4} {5}", entry.TimestampUtc,
                    sId, entry.RemoteEndpoint ?? "-", entry.EventName,
                    entry.VirtualPath ?? "-", entry.Detail ?? "-");
            }
        }

        /// <summary>
        /// A <see cref="TextWriter"/> that forwards every write to two underlying writers,
        /// used to duplicate console output to a log file.
        /// </summary>
        private sealed class TeeTextWriter : TextWriter
        {
            private readonly TextWriter _first;
            private readonly TextWriter _second;

            public TeeTextWriter(TextWriter first, TextWriter second)
            {
                _first = first;
                _second = second;
            }

            public override Encoding Encoding => _first.Encoding;

            public override void Write(char value)
            {
                _first.Write(value);
                _second.Write(value);
            }

            public override void Write(string value)
            {
                _first.Write(value);
                _second.Write(value);
            }

            public override void WriteLine(string value)
            {
                _first.WriteLine(value);
                _second.WriteLine(value);
            }

            public override void Flush()
            {
                _first.Flush();
                _second.Flush();
            }
        }

        #endregion // Helper Classes
    }
}