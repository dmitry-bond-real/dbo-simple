# Verification and Evidence

## Automated Checks

Run on Windows with the .NET Framework 4.8 Developer Pack installed:

```powershell
dotnet build Simple.FtpServer.sln
dotnet test Simple.FtpServer.sln
```

The current suite has 19 passing NUnit cases. It covers defaults and binding guards; INI creation, hashed credentials, update, reload preservation, and home overlap; CRLF framing and malformed/oversized input; ASCII boundary conversion; loopback login; generic unknown-user/wrong-password failures; FEAT, UTF-8 names with spaces, SIZE; passive PASV/EPSV and active PORT/EPRT upload/download round trips; read-only mutation rejection; staged-upload failure preservation; control-close cancellation; restart/stop/dispose behavior; and session-limit `421` rejection.

The test payloads do not constitute the required 1 MiB/100 MiB throughput or managed-memory measurements. The tests have not been run against FileZilla or WinSCP. Do not infer client interoperability or production readiness from the loopback suite.

## Requirement Map

| Requirement | Evidence or remaining verification |
| --- | --- |
| REQ-001 | `net48`, `LangVersion 7.3` projects; full solution build. |
| REQ-002 | Public server/options/store/filesystem APIs; `FtpServerLifecycleTests.StopIsIdempotentAndServerCanRestart`. |
| REQ-003 | Startup validates options, user snapshot, database location, and filesystem before bind; listener cleanup on startup failure. Occupied-port/no-leak and malformed initial database tests remain to add. |
| REQ-004 | Per-session state is held in `FtpControlSession`; sessions run independently and commands dispatch sequentially. Twenty-session isolation remains untested. |
| REQ-005 | Strict UTF-8 line reader; FEAT and UTF-8 path integration; PORT/EPRT/PASV/EPSV, SIZE, MDTM dispatch. MDTM has no direct test. |
| REQ-006 | `ControlLineReader` tests fragmented/coalesced commands, malformed UTF-8, and over-limit input. The exact 4,096-byte accepted boundary lacks a separate test. |
| REQ-007 | Authentication sequencing and generic failures exercised in `FtpServerIntegrationTests`; five-attempt disconnect and every pending-rename reset transition remain untested. |
| REQ-008 | Passive upload/download exercises 150/226; authentication exercises 230; denied writes exercise 550. Full reply-code matrix and transfer failure codes remain untested. |
| REQ-009 | PASV and EPSV integration tests; EPSV ALL, range exhaustion, foreign-peer rejection, and advertised-address configuration need manual/automated verification. |
| REQ-010 | PORT and EPRT active uploads/downloads are exercised; invalid octets, foreign address, low port, and non-IPv4 EPRT rejection need additional tests. |
| REQ-011 | Endpoint state is one-shot and disposed after transfer; control-close reclaims a passive listener. Foreign-peer discard, connect-before-command, replacement, and listener lifetime remain untested. |
| REQ-012 | Configurable connect/inactivity/listener deadlines are implemented. Timeout behavior and NAT/firewall setup require manual verification. |
| REQ-013 | Strict INI parser, username rules, duplicate/unknown record validation, home existence and overlap checks are implemented. Duplicate-key and malformed-record matrix needs tests. |
| REQ-014 | PBKDF2-HMAC-SHA256, random salt, bounded iteration count, and constant-work comparison implemented; account tests verify no plaintext and successful/failed auth. Iteration-boundary tests remain. |
| REQ-015 | Create/update/disable/password-change/reload APIs, masked host prompts, ACLs, and same-directory atomic replacement implemented. ACL audit and concurrent-write/reload tests remain. |
| REQ-016 | Generic failures, dummy derivation, four process-wide derivation slots, and per-connection failure count implemented. Disabled-user equivalence, fifth-attempt disconnect, and overload 421 remain untested. Reconnect attacks still require network-level controls. |
| REQ-017 | Canonical virtual paths, separator-aware confinement, reserved names, ADS/wildcard/backslash rejection implemented; basic traversal tests pass. Broader Windows path corpus remains. |
| REQ-018 | Reparse-point checks cover account homes, staging, and existing FTP path components. Junction/symlink tests and host ACL review remain. Host administrators must prevent untrusted local modification of roots and ancestors. |
| REQ-019 | Explicit permissions are enforced in filesystem operations; `ReadOnlyAccount_CannotModifyStorage` verifies STOR/DELE/MKD denial. Full per-command permission matrix remains. |
| REQ-020 | Bounded streaming and ASCII converter boundary tests; binary loopback round trip. 0-byte/1-byte/1 MiB/100 MiB corpus and memory measurement remain. |
| REQ-021 | Private staging, atomic replace/move, stale-file cleanup, per-path locks, and failed-upload preservation implemented; staging test passes. Conflict and crash-recovery cases remain. |
| REQ-022 | Conventional LIST/NLST formatter and limited switches implemented. Client-specific listing compatibility and option errors remain to verify. |
| REQ-023 | Defaults and option validation tests; explicit non-loopback acknowledgment and warning implemented. Exhaustive range/timeout/overlap boundary tests remain. |
| REQ-024 | Session limit rejection (`421`) is exercised at a configured limit of one; the twenty-first connection at the required limit of twenty and idle-client tests remain. |
| REQ-025 | Injectable sink logs event, session, endpoint, virtual path, bytes, duration, and replies; command arguments and secrets are not passed to logging. Add a log-capture disclosure test. |
| REQ-026 | This guide documents build, embedding, provisioning, permissions, client/network setup, security limits, and unsupported commands; this map and the license inventory are included. |
| REQ-027 | Solution restore/build/test passed on Windows; `dotnet list package --vulnerable` reported no known vulnerable packages from the configured sources. |
| REQ-028 | Automated coverage is partial. The remaining security, timeout, port, concurrency, client-compatibility, scale, and memory checks above are explicit follow-up verification work. |

## Acceptance Criteria Not Yet Verified

- SC-001: build and current tests pass, but not every requirement has automated or manually recorded evidence.
- SC-002: FileZilla and WinSCP versioned interoperability runs have not been performed.
- SC-003: all four negotiation commands are exercised on loopback; spoofing and negative cases remain.
- SC-004: current binary round trip uses 4,097 bytes, not the required size matrix or both transfer modes at each size.
- SC-005: basic traversal and read-only denials are tested; cross-user, reparse-point, and credential-log disclosure attempts need tests.
- SC-006: 20 concurrent transfers and 21st-session rejection have not been tested.
- SC-007: restart/rebind and one passive-listener cancellation are tested; 100-transfer reuse and shutdown under a real large transfer remain.
- SC-008: the Release 100 MiB managed-memory measurement has not been run.
