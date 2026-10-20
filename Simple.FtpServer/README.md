# Simple.FtpServer

An embeddable IPv4 FTP server for Windows applications targeting .NET Framework 4.8 and C# 7.3. The implementation provides a library, a minimal console host, a strict INI account store, a rooted local filesystem, and NUnit tests.

## Security Boundary

This is plain FTP. Usernames, passwords, and file contents are unencrypted while crossing the network. Use loopback or a trusted, isolated network only. Do not expose the server to the public Internet. The server refuses non-loopback control or passive bindings unless `AllowNonLoopbackPlainFtp` is explicitly set; the console equivalent is `--allow-nonloopback`. This is an acknowledgment, not encryption or a recommendation to expose FTP.

FTPS/TLS, SFTP, anonymous login, quotas, virtual mounts, resume, compression, and in-band `ABOR` are not implemented. See [the specification](docs/Simple.FtpServer.specs.md) and [verification evidence](docs/verification.md) for additional limitations and unverified acceptance criteria.

## Build and Test

On Windows, install the .NET Framework 4.8 Developer Pack and a .NET SDK. From the repository root:

```powershell
dotnet build Simple.FtpServer.sln
dotnet test Simple.FtpServer.sln
```

The host requires the .NET Framework 4.8 runtime when executed. Package versions are pinned in the project files; package and license inventory is in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Provision an Account

Create the account home before provisioning it. Keep the users INI and staging directory outside every account home. Use a private, access-controlled configuration directory. The store applies protected Windows ACLs for the current Windows identity, SYSTEM, Administrators, and any operator SIDs passed to its constructor.

```powershell
New-Item -ItemType Directory -Force C:\FtpData\alice | Out-Null
dotnet run --project src\Simple.FtpServer.Host -- user --users C:\FtpConfig\users.ini add alice C:\FtpData\alice List,Read,Write,Delete,Rename
```

The password and confirmation are read from a masked console prompt; never pass a password on the command line. Passwords must contain 12-256 Unicode text elements and cannot contain CR, LF, or NUL. Empty permissions are supported with `none`.

Account operations:

```powershell
dotnet run --project src\Simple.FtpServer.Host -- user --users C:\FtpConfig\users.ini list
dotnet run --project src\Simple.FtpServer.Host -- user --users C:\FtpConfig\users.ini disable alice
dotnet run --project src\Simple.FtpServer.Host -- user --users C:\FtpConfig\users.ini enable alice
dotnet run --project src\Simple.FtpServer.Host -- user --users C:\FtpConfig\users.ini password alice
dotnet run --project src\Simple.FtpServer.Host -- user --users C:\FtpConfig\users.ini update alice C:\FtpData\alice List,Read true
```

The INI file stores PBKDF2-HMAC-SHA256 records, not plaintext passwords. The default is 600,000 iterations, a fresh 16-byte random salt, and a 32-byte derived key. Treat the file and its parent directory as credentials.

## Run the Host

```powershell
New-Item -ItemType Directory -Force C:\FtpPrivate\staging | Out-Null
dotnet run --project src\Simple.FtpServer.Host -- serve --users C:\FtpConfig\users.ini --staging C:\FtpPrivate\staging
```

Defaults are control `127.0.0.1:2121`, passive ports `50000-50049`, 20 sessions, 300-second control idle timeout, 30-second data-connect timeout, 60-second transfer inactivity timeout, 30-second passive-listener lifetime, and a 65,536-byte transfer buffer. `Ctrl+C` stops the host.

For a trusted network, configure `--bind`, `--passive-bind`, `--advertise`, `--passive-start`, and `--passive-end` as appropriate, and explicitly include `--allow-nonloopback`. Forward the control port and the complete passive port range through the firewall/NAT, and set `--advertise` to the reachable IPv4 address. Active mode requires the client to be reachable from the server and can fail through NAT.

## Embed the Library

```csharp
var users = new IniFtpUserStore(usersIniPath);
var fileSystem = new LocalFtpFileSystem(privateStagingDirectory);
var server = new FtpServer(options, users, fileSystem, logSink);

await server.StartAsync(cancellationToken);
// The accept loop runs in the background after StartAsync completes.
await server.StopAsync(cancellationToken);
server.Dispose();
```

`FtpServerOptions.Validate()` requires absolute users/staging paths and validates bind addresses, ports, timeouts, and limits. The users database is loaded and validated before the control listener binds. Dependencies supplied to `FtpServer` remain owned by the caller. `IFtpUserStore`, `IFtpFileSystem`, and `IFtpLogSink` are the extension/testing boundaries; filesystem operations enforce the account permission set and root confinement.

Supported permissions are `List`, `Read`, `Write`, `Delete`, and `Rename`. Unsupported FTP commands include `AUTH`, `REST`, `APPE`, `MLSD`, `SITE`, and `ABOR`; they receive `502`. Listing accepts bare `LIST`/`NLST` and `LIST -a`/`LIST -l`; it does not expand wildcards.