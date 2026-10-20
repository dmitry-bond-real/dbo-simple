# Specification: Embeddable FTP Server for .NET Framework 4.8

**Created:** 2026-10-15  
**Status:** Implementation-ready example  
**Audience:** AI coding agent and reviewing developer

## 1. Scope Baseline

Build a small, maintainable FTP server component that a Windows application can
embed. Target .NET Framework 4.8, not .NET Core or modern .NET. Include a console
host, an independently managed INI user database, and automated tests.

The scope is derived from the requested FTP component, active/passive modes, and
local user/password management. There is no existing application to preserve.
This document defines the complete version-one scope; MUST is mandatory, SHOULD
is recommended, and MAY is optional.

### Assumptions and Security Boundary

- Windows, IPv4, local NTFS storage, and a single server process per user database.
- Plain FTP only. Credentials and file contents are unencrypted on the network.
  Deploy only on loopback or a trusted, isolated network; do not expose it to the
  public Internet. Password hashing protects stored credentials, not network traffic.
- FTPS/TLS and SFTP are out of scope. SFTP is a different protocol.
- No anonymous login, GUI, Windows-service installer, quotas, virtual mounts,
  database server, transfer resume, compression, or multi-server coordination.
- No production readiness claim outside this security boundary. Add FTPS under
  a separately reviewed specification before use on untrusted networks.

## 2. User Scenarios and Testing

### Scenario A: Authenticate and Transfer Files (P1)

As an FTP user, I can authenticate with my own account, browse my home directory,
and upload/download files without accessing another user's files.

1. Given an enabled user, when valid credentials are supplied, then login succeeds
   and the initial virtual working directory is `/`.
2. Given a wrong password, unknown user, or disabled account, when login is tried,
   then the server returns the same generic `530` failure.
3. Given a binary file, when uploaded and downloaded using either active or
   passive mode, then its length and SHA-256 digest remain unchanged.
4. Given a user restricted to reads, when a modifying command is issued, then it
   fails with `550` and storage remains unchanged.

### Scenario B: Manage Accounts Independently (P1)

As an operator, I can create, disable, and update FTP accounts without Windows
accounts or an external database.

1. Given a newly created account, when the operator inspects the user INI file,
   then it contains a salt and password hash but no plaintext password.
2. Given a malformed replacement database, when reload is requested, then it
   fails with a sanitized error and the previous valid snapshot remains active.
3. Given a disabled user in a successfully reloaded database, when that user
   attempts a new login, then authentication fails.

### Scenario C: Operate Reliably (P2)

As an embedding application, I can configure, start, observe, and stop the server.

1. Given an unavailable control port or invalid configuration, when startup is
   requested, then it fails without leaving bound sockets or background workers.
2. Given an active transfer, when shutdown is requested, then all sessions,
   listeners, streams, and temporary uploads are closed within the shutdown limit.
3. Given repeated failed transfers, when clients reconnect, then other sessions
   remain usable and passive ports are reclaimed.

## 3. Requirements

### Component and Lifecycle

- **REQ-001:** Produce a C# class library targeting `net48` and C# 7.3, plus a
  minimal console host. Do not use APIs available only in modern .NET. Use async
  socket/stream operations supported by .NET Framework 4.8.
- **REQ-002:** Expose `FtpServer`, `FtpServerOptions`, `IFtpUserStore`, and a
  filesystem abstraction suitable for focused testing. Provide
  `Task StartAsync(CancellationToken)`, `Task StopAsync(CancellationToken)`, and
  `IDisposable`. Start completes after binding; it does not await the accept loop.
  Reject starting an already running instance; repeated stop/dispose is harmless.
  A stopped, non-disposed instance can restart. The server does not dispose
  externally supplied dependencies.
- **REQ-003:** Validate all options and the initial user database before accepting
  clients. Stop accepting connections at shutdown, cancel in-flight work, release
  resources within 10 seconds, and observe background-task exceptions.
- **REQ-004:** Keep each session's identity, working directory, transfer type,
  rename state, and data endpoint isolated. Process commands sequentially per
  session, but service different sessions concurrently. Allow one transfer per
  session; no multiplexing or command pipelining guarantee is required.

### Control Protocol

- **REQ-005:** Implement RFC 959 control framing and the command subset below,
  RFC 2428 `EPRT`/`EPSV` for IPv4, RFC 3659 `SIZE`/`MDTM` in binary mode, and
  RFC 2389 `FEAT`. Use UTF-8 command paths and listings and advertise `UTF8` only
  when implemented. Keywords are case-insensitive; path arguments retain spaces
  and case. Reject malformed UTF-8 and CR/LF injection.
- **REQ-006:** Parse complete CRLF-terminated lines across fragmented TCP reads
  and multiple lines per read. Limit each command to 4,096 bytes including CRLF;
  return `500` and close for an oversized line. Do not split path arguments on
  whitespace. Reply text must not expose physical paths, exceptions, or secrets.
- **REQ-007:** Before login, allow only `USER`, `PASS`, `FEAT`, `SYST`, `NOOP`,
  and `QUIT`; return `530` for other recognized commands. A new `USER` clears
  prior authentication and pending data/rename state. `PASS` before `USER`
  returns `503`. Clear pending rename state after any command except its matching
  `RNTO`. Unknown commands return `502`; syntax errors return `501`.
- **REQ-008:** Return `220` on connect, `331` for a syntactically valid `USER`,
  `230` on successful `PASS`, and `221` on `QUIT`. Return `150` before opening
  or accepting a transfer connection and `226` only after successful completion
  and data-stream closure. Use `425` when no data connection can be established,
  `426` for an interrupted established transfer, `451` for local processing
  failure, and `550` for unavailable/forbidden paths. Do not send success after
  transfer failure. Handle multiline `FEAT` replies correctly.

| Commands | Required behavior |
| --- | --- |
| `USER`, `PASS`, `QUIT` | Authenticate or disconnect as specified above. |
| `SYST`, `FEAT`, `NOOP` | Return `215 UNIX Type: L8`, accurate features, and `200`, respectively. |
| `PWD`, `CWD`, `CDUP` | Report/change virtual directories; `PWD` uses `257`, successful changes use `250`. `CDUP` at root stays at root. |
| `TYPE I`, `TYPE A` | Select binary or ASCII; default to binary. Return `200`; unsupported types return `504`. |
| `PASV`, `EPSV`, `PORT`, `EPRT` | Select the next transfer endpoint; return `227`, `229`, `200`, or `200`. |
| `LIST`, `NLST` | List an optional directory or exact file path; no wildcard expansion. Empty listings succeed. |
| `RETR`, `STOR` | Download or upload one file, applying permissions and transfer type. |
| `DELE`, `MKD`, `RMD` | Delete a file, create a directory, or remove an empty directory; successful replies are `250`, `257`, `250`. |
| `RNFR`, `RNTO` | Rename within the user's root; use `350` then `250`; `RNTO` without valid `RNFR` returns `503`. Never overwrite an existing destination. |
| `SIZE`, `MDTM` | Return binary byte length (`213`) or UTC `yyyyMMddHHmmss` modification time (`213`) for a file. Reject `SIZE` in ASCII mode with `504`. |
| `OPTS UTF8 ON` | Return `200`; unsupported `OPTS` arguments return `501`. UTF-8 remains enabled. |

Commands not listed, including `AUTH`, `REST`, `APPE`, `MLSD`, `SITE`, and `ABOR`,
are unsupported and return `502`. A client can cancel an in-progress transfer by
closing its data/control connection. In-band abort during a transfer is not a
version-one capability. Do not advertise unsupported extensions.

### Active and Passive Data Connections

- **REQ-009:** Support `PASV` and `EPSV` using a configurable inclusive passive
  port range. `PASV` advertises the configured IPv4 address or the local control
  endpoint address; `EPSV` returns only the port. `EPSV ALL` disables subsequent
  `PORT`, `EPRT`, and `PASV` for that session. Reject unsupported address families
  with `522`. Binding failure or range exhaustion returns `425` without leaks.
- **REQ-010:** Support `PORT h1,h2,h3,h4,p1,p2` and `EPRT |1|address|port|`;
  accept other valid EPRT delimiter characters. Validate octets, delimiters, and
  port range. Require the destination IP to equal the control peer IP and the
  destination port to be 1024-65535. Reject violations with `501`. Connect only
  when a transfer command executes. Do not promise source port 20.
- **REQ-011:** On passive acceptance, require the data peer IP to equal the
  control peer IP. Discard foreign connections while preserving the original
  timeout. Each passive listener is exclusive to one session; replacing a data
  mode closes the previous listener. Close and clear the endpoint after every
  attempted transfer, timeout, logout, or disconnect. Require fresh negotiation
  before every listing or file transfer. Allow clients to connect passively
  either before or after the transfer command, as common FTP clients do.
- **REQ-012:** Implement configurable data-connect and transfer-inactivity
  deadlines, not a fixed total-transfer duration. Reset inactivity on actual
  progress. Reclaim a passive listener if no transfer command arrives within
  the configured passive-listener lifetime. Document NAT/firewall setup: forward
  the control port and entire passive range and configure the advertised address.
  Active mode requires a reachable client and may fail through NAT.

### Local User Database

- **REQ-013:** Provide an INI-backed `IFtpUserStore`, independent of OS accounts.
  Each account has a unique username, enabled flag, absolute home directory,
  password record, and permission set. Usernames use ASCII letters, digits,
  underscore, hyphen, or dot, with length 1-64; compare them ordinally without
  case. Reject duplicate sections/keys, unknown permission names, malformed
  records, missing homes, and homes that overlap another account's home.
- **REQ-014:** Never store plaintext or reversibly encrypted passwords. Use
  PBKDF2-HMAC-SHA256 with a fresh cryptographically random salt of at least
  16 bytes, a 32-byte derived key, and a default 600,000 iterations. Store the
  algorithm version and iteration count per account. Accept only supported
  versions and iteration counts from 100,000 through 2,000,000. Compare derived
  keys without early exit; use a .NET Framework-compatible implementation.
- **REQ-015:** Provide local create/update/disable/password-change operations
  through an administrative API and console commands. Read passwords from a
  masked console prompt, never a command-line argument or log. Serialize local
  writes and replace the database atomically using a temporary file on the same
  volume. Provide explicit reload with immutable snapshot replacement; failed
  reload keeps the last valid snapshot. Account changes apply to new logins;
  existing sessions keep their authenticated snapshot until disconnected.
- **REQ-016:** Return indistinguishable login failures for unknown, disabled,
  and wrong-password accounts; perform equivalent dummy hashing for unknown or
  disabled accounts. Disconnect after five failed `PASS` attempts per connection.
  Bound simultaneous password derivations to four process-wide and close excess
  pending login attempts with `421`. Document that reconnect-based attacks are
  not prevented by the per-connection limit and require network-level controls.

### Filesystem and Permissions

- **REQ-017:** Expose each user's home as virtual `/`. Resolve relative paths
  against the session working directory and canonicalize before every operation,
  including source and destination of rename. Reject traversal outside the root,
  rooted Windows/UNC/device paths, backslashes, drive prefixes, alternate data
  streams, NUL, wildcard paths, and reserved Windows device names. Check root
  boundaries using a separator-aware, case-insensitive comparison, not a simple
  string prefix. Virtual absolute paths such as `/reports/a.txt` remain valid.
- **REQ-018:** Reject reparse points, junctions, and symbolic links in home roots
  and all existing path components, including the target. Validate parent paths
  for new files/directories. The host must prevent untrusted local processes
  from modifying roots or ancestors; concurrent local link replacement is
  outside the threat model. Never expose configuration, credentials, logs, or
  staging files through FTP. Keep these outside every user root.
- **REQ-019:** Enforce explicit permissions: `List` for browsing/listing,
  `Read` for `RETR`/`SIZE`/`MDTM`, `Write` for `STOR`/`MKD`, `Delete` for
  `DELE`/`RMD`, and `Rename` for `RNFR`/`RNTO`. Renames require both paths to
  be authorized. An empty set grants no filesystem access. Root deletion and
  root rename are always forbidden. No command may bypass permission checks.
- **REQ-020:** Stream transfers using bounded buffers, independent of file size.
  Binary transfers preserve bytes. ASCII uploads translate CRLF to Windows CRLF
  and CR-NUL to literal CR; ASCII downloads encode local CRLF as CRLF and lone
  CR as CR-NUL. Preserve lone LF and correctly handle sequences across buffer
  boundaries without decoding file contents as text. Listings always use UTF-8
  and CRLF, irrespective of selected transfer type.
- **REQ-021:** Stage uploads outside all user roots on the same volume, then
  publish using atomic move/replace only after successful data-stream completion.
  Preserve the previous target on failure; delete staging files after failure.
  Serialize conflicting mutations of the same canonical path and return `450`
  for conflicting access. Keep the target parent stable during publication.
  Clean up only this server's stale staging files at startup. A clean data EOF
  completes `STOR`; FTP cannot detect a client that cleanly closes after sending
  fewer bytes than intended. Do not claim content-length validation.
- **REQ-022:** `LIST` emits conventional UNIX-style entries compatible with the
  tested clients; `NLST` emits names only, excluding `.` and `..`. Never include
  CR/LF-containing filenames in listings. Support bare `LIST` and client-style
  `LIST -a`/`LIST -l`; show normal and hidden entries consistently. Other listing
  switches return `501`. Document this deliberately limited listing syntax.

### Configuration and Diagnostics

- **REQ-023:** Provide the options below, validate port/range/timeout bounds and
  positive limits, and reject overlapping control/passive ports. Binding beyond
  loopback requires explicit `AllowNonLoopbackPlainFtp=true` acknowledgment and
  a startup warning. Never silently enable external access.

| Option | Default |
| --- | --- |
| Control bind IPv4 / port | `127.0.0.1` / `2121` |
| Passive bind IPv4 | Same as control bind address |
| Passive advertised IPv4 | Local control endpoint address |
| Passive port range | `50000-50049` inclusive |
| Maximum control sessions | `20` |
| Control idle timeout | `300` seconds; suspended during a transfer |
| Data connect timeout | `30` seconds |
| Transfer inactivity timeout | `60` seconds |
| Passive listener lifetime | `30` seconds until a transfer command |
| Shutdown limit | `10` seconds |
| Transfer buffer size | `65536` bytes |
| Users INI and private staging directory | Required absolute paths outside all roots |
| Allow non-loopback plain FTP | `false` |

- **REQ-024:** Reject connections over the configured session limit with `421`
  and close them. Enforce deadlines on clients that send no complete commands.
  A failed session must not stop the listener or another session.
- **REQ-025:** Accept an injected logging sink. Record timestamps, session IDs,
  remote endpoints, authentication outcome, command names, virtual paths,
  transfer byte counts, duration, reply codes, and sanitized errors. Never log
  `PASS` arguments, password records, raw command lines, or file contents.

### Delivery and Verification

- **REQ-026:** Supply the solution, library, example console host, administrative
  utility, automated unit/integration tests, and operator/developer documentation.
  Document build commands, embedding, account provisioning, permissions, client
  setup, passive NAT configuration, security limits, and unsupported commands.
  Include a dependency/license inventory and no embedded default credentials.
- **REQ-027:** Build and run tests on Windows with the .NET Framework 4.8
  developer pack. Use established compatible libraries for INI parsing and tests
  where practical; pin dependencies and avoid unsupported runtime requirements.
  A maintained compatible FTP engine MAY be used if all required semantics and
  embeddability are demonstrated. Do not substitute an external FTP service.
- **REQ-028:** Automate tests covering all scenarios, command framing, invalid
  state transitions, UTF-8 names, ASCII buffer boundaries, permissions, path
  escapes/reparse points, hash verification, atomic user reload, endpoint spoofing,
  port exhaustion, timeouts, failed uploads, concurrent sessions, and shutdown.
  Use loopback and temporary directories; no Internet or administrator rights.
  Every requirement must map to at least one automated test or explicitly
  documented manual verification step.

### Key Entities

- **Account:** Username, enabled state, home, password record, permissions.
- **Password record:** Version, algorithm, iterations, Base64 salt and hash.
- **Session:** Authenticated account snapshot, virtual directory, selected type,
  endpoint state, deadlines, and pending rename source.
- **Server options:** Validated network, storage, timeout, and capacity settings.
- **Transfer:** Session owner, operation, virtual path, staging path if needed,
  bytes transferred, outcome, and resource lifetime.

## 4. INI Storage Contract

Use UTF-8 INI with one `[user:<username>]` section per account. Values extend
from the first `=` to end of line and are trimmed; backslashes are literal.
Allow blank lines and full-line `;` comments, but no inline comments, interpolation,
or duplicate keys. Key names are case-insensitive. Reject unknown keys rather
than silently accepting misspellings. Apply filesystem ACLs so only the host
identity and authorized operators can read/write the database and its directory.

The following illustrates the schema only. `GENERATED_BASE64_SALT` and
`GENERATED_BASE64_HASH` must be replaced by the provisioning utility, and the
home must already exist; this example is intentionally not a runnable account.

```ini
[user:alice]
Enabled=true
HomeDirectory=C:\FtpData\alice
PasswordVersion=1
PasswordAlgorithm=PBKDF2-HMAC-SHA256
PasswordIterations=600000
PasswordSalt=GENERATED_BASE64_SALT
PasswordHash=GENERATED_BASE64_HASH
Permissions=List,Read,Write,Delete,Rename
```

Empty passwords are prohibited. Accept passwords of 12-256 Unicode characters,
encode them as UTF-8 without normalization before hashing, and reject CR, LF,
and NUL. Do not trim passwords. Account-management errors may be specific for
local operators, but network authentication errors must remain generic.

## 5. Success Criteria

| ID | Acceptance measure |
| --- | --- |
| SC-001 | All requirements have evidence; the Windows build and automated tests pass. |
| SC-002 | Current stable FileZilla Client and WinSCP can log in, browse, upload, download, rename, and delete using both active and passive FTP. Record exact client versions and settings. |
| SC-003 | Automated tests exercise `PORT`, `EPRT`, `PASV`, and `EPSV` separately, including peer-address restrictions and fresh negotiation for each transfer. |
| SC-004 | Zero-byte, 1-byte, 1 MiB, and 100 MiB binary files round-trip with identical length and SHA-256 in both data modes. Unicode names and paths containing spaces work. |
| SC-005 | All tested traversal, cross-user access, unauthorized writes, and credential-log disclosure attempts fail without changing protected files. |
| SC-006 | Twenty simultaneous loopback sessions each complete a 1 MiB transfer without cross-session state contamination; a twenty-first connection is rejected with `421`. |
| SC-007 | After 100 sequential transfers, passive ports are reusable; shutdown during transfer completes within 10 seconds and the control port can immediately be rebound. |
| SC-008 | A 100 MiB binary transfer uses at most 32 MiB additional managed memory over an authenticated idle session in a Release build after warm-up, measured by a documented Windows test harness. |

## 6. Instructions to the Implementing AI

1. Treat this specification as the source of truth. Keep version one within the
   stated scope; raise contradictions or missing security prerequisites before
   implementing conflicting behavior.
2. Organize responsibilities around protocol/session handling, data connections,
   authentication/user storage, and rooted filesystem access. Avoid unnecessary
   frameworks or generalized plugin systems.
3. Write focused tests alongside implementation. Cover the security and failure
   paths as well as successful transfers. Use bounded cancellation and explicit
   ownership for sockets, streams, and background work.
4. Deliver runnable code, not sketches or unimplemented methods. Do not add TLS,
   SFTP, a UI, or unsupported commands without a separate scope decision.
5. Finish with exact build/test commands, a requirement-to-test evidence table,
   client compatibility results, and known limitations. Mark anything not executed
   as unverified; never infer successful interoperability from unit tests alone.