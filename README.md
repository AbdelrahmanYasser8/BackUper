# BackUper

A Windows file copy tool that can copy locked files (including exclusively locked files) without requiring administrator privileges or closing the locking application.

## Features

- **Copies locked files** — Handles files locked by other processes
- **No admin required** — Runs as standard user
- **Doesn't close apps** — Strategy 4 reads via handle duplication without terminating the locking process
- **5 fallback strategies** — Tries progressively more powerful methods until one works
- **Live progress** — Real-time percentage display every 5 seconds during copy; interactive confirmation before shutdown-based strategy
- **No size limit** — Streams directly to disk in chunks, supports files of any size
- **Single command** — `dotnet run copy <source> <destination>`

## Requirements

- **Windows** (uses Win32 APIs: `ntdll.dll`, `kernel32.dll`, `rstrtmgr.dll`)
- **.NET 8.0 Runtime** or SDK

Install .NET 8:
```cmd
winget install Microsoft.DotNet.Runtime.8
```
or download from https://dotnet.microsoft.com/en-us/download/dotnet/8.0

## Usage

```cmd
dotnet run copy "C:\path\to\locked\file.pst" "D:\backup\file.pst"
```

**Arguments:**
- `<source>` — Path to the locked file to copy
- `<destination>` — Where to write the copy

## How It Works (5 Strategies)

The tool tries strategies in order, stopping at the first success:

| # | Strategy | How it works | Admin? | Closes app? |
|--:|----------|--------------|:------:|:-----------:|
| 1 | **Normal copy** | Standard `File.Copy` | No | No |
| 2 | **Shared FileStream** | Opens source with `FileShare.ReadWrite` flags | No | No |
| 3 | **esentutl** | Shells out to Windows built-in ESENT utility | No | No |
| 4 | **Handle duplication** | Scans system handles, duplicates the locking process's file handle into our process, reads via chunked memory-mapped I/O (256 MB windows) | **No** | **No** |
| 5 | **Restart Manager** | Registers file with Windows Restart Manager API, shuts down the locking app, copies the file, then restarts the app | No | **Yes** (last resort) |

### How Handle Duplication Works

Strategy 4 is what makes BackUper unique. It can read an exclusively locked file without admin rights and without closing the application. Here's how it works step by step:

1. **Scan all system handles** — Calls the undocumented NT API `NtQuerySystemInformation(SystemHandleInformation)` to get a dump of **every open handle** across every running process on the system (files, registry keys, pipes, etc.)

2. **Find the locking process** — For each handle in the dump, opens the owning process with `OpenProcess(PROCESS_DUP_HANDLE)` and tries to duplicate the handle into BackUper's process space via `DuplicateHandle`. Resolves the duplicated handle to a file path using `GetFinalPathNameByHandle` and checks if it matches the target file

3. **Duplicate the handle from the kernel** — Once the matching handle is found, `DuplicateHandle` clones it from the locking process directly into BackUper. The kernel allows this because the duplicate inherits the **same access rights** as the original handle — even if the original was opened with `FileShare.None`

4. **Read the file through the duplicated handle** — Creates a memory-mapped file view via `CreateFileMapping` + `MapViewOfFile` on the duplicated handle. For files under 256 MB, the entire file is mapped at once. For larger files, a **chunked approach** is used: 256 MB windows are mapped, copied, and unmapped in sequence, supporting files of any size while keeping memory usage constant

5. **Write to destination** — The bytes read from the mapped view are streamed directly to the output file in 16 MB sub-chunks

> **Why this works:** The Windows kernel maintains a global handle table. Even if a process opens a file with `FileShare.None` (exclusive lock), the underlying file object in the kernel is still accessible. By duplicating the handle, BackUper gains the same kernel-level access as the locking process — reading is permitted because the original handle already has read rights.

## Supported Lock Types

| Lock type | Example | Strategy that works |
|-----------|---------|---------------------|
| Shared read (`FileShare.Read`) | Most apps | 1 or 2 |
| Shared read/write (`FileShare.ReadWrite`) | Chrome, Office | 2 |
| Exclusive (`FileShare.None`) | Custom apps, some DBs | **4** |
| ESENT databases | `ntds.dit`, `Windows.edb` | 3 |
| Unclosable system locks | LSASS, kernel | 5 |

## Building from Source

```cmd
git clone <repo>
cd BackUper\BackUper
dotnet build -c Release
```

Output: `bin\Release\net8.0\BackUper.exe`

## Limitations

- **Windows only** (relies on undocumented `NtQuerySystemInformation`)
- **Local files only** — no network paths for locked files (Strategy 4 requires local handle enumeration)
- **Strategy 5** requires the locking app to be restartable via Restart Manager (most GUI apps are)
- **Separate WAL files not copied** — if a database uses a separate `-wal`/`-shm` file (e.g., SQLite in WAL mode), only the main file is copied; uncommitted writes in the WAL file are missed

## License

MIT