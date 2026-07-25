# BackUper

A Windows file copy tool that can copy locked files (including exclusively locked files) without requiring administrator privileges or closing the locking application.

## Features

- **Copies locked files** — Handles files locked by other processes
- **No admin required** — Runs as standard user
- **5 fallback strategies** — Tries progressively more powerful methods until one works
- **Single command** — `BackUper copy <source> <destination>`

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
BackUper copy "C:\path\to\locked\file.pst" "D:\backup\file.pst"
```

**Arguments:**
- `<source>` — Path to the locked file to copy
- `<destination>` — Where to write the copy

## How It Works (5 Strategies)

The tool tries strategies in order, stopping at the first success:

| # | Strategy | How it works | Admin? | Closes app? |
|---|----------|--------------|--------|-------------|
| 1 | **Normal copy** | `File.Copy` | No | No |
| 2 | **Shared FileStream** | Opens with `FileShare.ReadWrite` | No | No |
| 3 | **esentutl** | Windows built-in ESENT utility | No | No |
| 4 | **Handle duplication** | Enumerates system handles via `NtQuerySystemInformation`, duplicates the locking process's file handle into our process, reads via `CreateFileMapping`/`MapViewOfFile` | **No** | **No** |
| 5 | **Restart Manager** | Registers file with `RmStartSession`, shuts down locking app via `RmShutdown`, copies, restarts app via `RmRestart` | No | **Yes** (last resort) |

### Strategy 4 Details (The Key Feature)

Strategy 4 is what makes BackUper unique for exclusive locks:

1. Calls `NtQuerySystemInformation(SystemHandleInformation)` to get all open handles system-wide
2. For each handle, opens the owning process with `PROCESS_DUP_HANDLE`
3. Calls `DuplicateHandle` to clone the file handle into our process
4. Uses `GetFinalPathNameByHandle` to verify it's the target file
5. Reads the file content via memory-mapped I/O (`CreateFileMapping` + `MapViewOfFile`) — this reads from the kernel file cache, bypassing the exclusive lock
6. Writes the bytes to the destination

This works because the kernel allows duplicating an existing valid handle even if the original was opened with `FileShare.None`. The duplicated handle inherits the same access rights.

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

## License

MIT