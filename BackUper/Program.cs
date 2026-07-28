using System.Diagnostics;
using System.Threading;
using BackUper.Services;

if (args.Length < 3)
{
    Console.WriteLine("Usage: BackUper copy <input> <output>");
    return;
}

var inputPath = Path.GetFullPath(args[1]);
var outputPath = Path.GetFullPath(args[2]);

if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
{
    Console.WriteLine($"Error: Input not found: {inputPath}");
    return;
}

if (Directory.Exists(inputPath))
{
    CopyDirectory(inputPath, outputPath);
}
else
{
    CopyFileWithFallback(inputPath, outputPath);
}

static void CopyDirectory(string sourceDir, string destDir)
{
    if (!Directory.Exists(sourceDir))
    {
        Console.WriteLine($"Error: Source directory not found: {sourceDir}");
        return;
    }

    if (!Directory.Exists(destDir))
    {
        Directory.CreateDirectory(destDir);
    }

    var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
    Console.WriteLine($"Found {files.Length} file(s) to copy");
    Console.WriteLine();

    int successCount = 0;
    int failCount = 0;

    foreach (var file in files)
    {
        string relativePath = Path.GetRelativePath(sourceDir, file);
        string destFile = Path.Combine(destDir, relativePath);

        Console.WriteLine($"[{successCount + failCount + 1}/{files.Length}] {relativePath}");

        string? destDirOnly = Path.GetDirectoryName(destFile);
        if (!string.IsNullOrEmpty(destDirOnly) && !Directory.Exists(destDirOnly))
            Directory.CreateDirectory(destDirOnly!);

        bool success = CopyFileWithFallback(file, destFile, silent: true);
        if (success)
            successCount++;
        else
            failCount++;

        Console.WriteLine();
    }

    Console.WriteLine($"=== Summary ===");
    Console.WriteLine($"Total:  {files.Length}");
    Console.WriteLine($"Copied: {successCount}");
    Console.WriteLine($"Failed: {failCount}");
}

static bool CopyFileWithFallback(string inputPath, string outputPath, bool silent = false)
{
    if (!silent)
    {
        var totalSize = new FileInfo(inputPath).Length;
        Console.WriteLine($"Source: {inputPath} ({totalSize:N0} bytes)");
        Console.WriteLine($"Target: {outputPath}");
        Console.WriteLine();
        Console.WriteLine("Strategies tried:");
    }

    // Strategy 1: Normal copy
    try
    {
        using var src = new FileStream(inputPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 16 * 1024 * 1024, FileOptions.SequentialScan);
        using var dst = new FileStream(outputPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 16 * 1024 * 1024, FileOptions.SequentialScan);

        long totalSize1 = src.Length;
        long bytesCopied1 = 0;
        bool progressStarted1 = false;
        Timer? timer1 = null;
        if (!silent)
        {
            timer1 = new Timer(_ =>
            {
                long copied = Interlocked.Read(ref bytesCopied1);
                double pct = totalSize1 > 0 ? (double)copied / totalSize1 * 100.0 : 0;
                if (!progressStarted1)
                {
                    progressStarted1 = true;
                    Console.WriteLine();
                }
                Console.Write($"\r  [1] Normal copy .............. {pct:F1}% | {copied:N0} / {totalSize1:N0} bytes   ");
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        try
        {
            byte[] buffer = new byte[16 * 1024 * 1024];
            int bytesRead;
            while ((bytesRead = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                dst.Write(buffer, 0, bytesRead);
                Interlocked.Add(ref bytesCopied1, bytesRead);
            }
        }
        finally
        {
            timer1?.Dispose();
        }

        if (!silent)
        {
            if (progressStarted1) Console.Write($"\r{new string(' ', 80)}\r");
            Console.WriteLine("  [1] Normal copy .............. SUCCESS");
            PrintResult(outputPath);
        }
        return true;
    }
    catch (IOException) { if (!silent) Console.WriteLine("  [1] Normal copy .............. SKIPPED (file locked)"); }

    // Strategy 2: Shared-access FileStream
    try
    {
        using var src = new FileStream(inputPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete | FileShare.Write);
        using var dst = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);

        long totalSize2 = src.Length;
        long bytesCopied2 = 0;
        bool progressStarted2 = false;
        Timer? timer2 = null;
        if (!silent)
        {
            timer2 = new Timer(_ =>
            {
                long copied = Interlocked.Read(ref bytesCopied2);
                double pct = totalSize2 > 0 ? (double)copied / totalSize2 * 100.0 : 0;
                if (!progressStarted2)
                {
                    progressStarted2 = true;
                    Console.WriteLine();
                }
                Console.Write($"\r  [2] Shared-access copy ....... {pct:F1}% | {copied:N0} / {totalSize2:N0} bytes   ");
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        try
        {
            byte[] buffer = new byte[16 * 1024 * 1024];
            int bytesRead;
            while ((bytesRead = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                dst.Write(buffer, 0, bytesRead);
                Interlocked.Add(ref bytesCopied2, bytesRead);
            }
        }
        finally
        {
            timer2?.Dispose();
        }

        if (!silent)
        {
            if (progressStarted2) Console.Write($"\r{new string(' ', 80)}\r");
            Console.WriteLine("  [2] Shared-access copy ....... SUCCESS");
            PrintResult(outputPath);
        }
        return true;
    }
    catch (IOException) { if (!silent) Console.WriteLine("  [2] Shared-access copy ....... SKIPPED (exclusive lock)"); }

    // Strategy 3: esentutl (built-in Windows tool)
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "esentutl.exe",
            Arguments = $"/y \"{inputPath}\" /d \"{outputPath}\" /o",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi)!;

        if (!silent)
        {
            var spinner = new[] { '|', '/', '-', '\\' };
            int spinIdx = 0;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!process.WaitForExit(200))
            {
                if (DateTime.UtcNow > deadline) break;
                Console.Write($"\r  [3] esentutl copy ............ RUNNING {spinner[spinIdx++ % 4]}  ");
            }
            Console.Write($"\r{new string(' ', 80)}\r");
        }
        else
        {
            process.WaitForExit(60000);
        }

        if (process.ExitCode == 0 && File.Exists(outputPath))
        {
            if (!silent) Console.WriteLine("  [3] esentutl copy ............ SUCCESS");
            if (!silent) PrintResult(outputPath);
            return true;
        }
        if (!silent) Console.WriteLine("  [3] esentutl copy ............ FAILED (exit code " + process.ExitCode + ")");
    }
    catch { if (!silent) Console.WriteLine("  [3] esentutl copy ............ SKIPPED (not available)"); }

    // Strategy 4: Duplicate handle (no admin, no app close)
    try
    {
        Action<HandleDuplicateCopy.ChunkProgress>? onProgress = null;
        bool progressStarted = false;
        if (!silent)
        {
            onProgress = (p) =>
            {
                if (!progressStarted)
                {
                    progressStarted = true;
                    Console.WriteLine();
                }
                Console.Write($"\r  [4] Handle duplicate ......... Chunk {p.CompletedChunks}/{p.TotalChunks} | {p.CurrentChunkPercent:F1}% of chunk | Overall: {p.OverallPercent:F1}%   ");
            };
        }

        if (HandleDuplicateCopy.TryCopy(inputPath, outputPath, onProgress))
        {
            if (!silent)
            {
                if (progressStarted) Console.Write($"\r{new string(' ', 80)}\r");
                Console.WriteLine("  [4] Handle duplicate ......... SUCCESS");
                PrintResult(outputPath);
            }
            return true;
        }
        if (!silent) Console.WriteLine("  [4] Handle duplicate ......... FAILED (no matching handle found)");
    }
    catch (Exception ex)
    {
        if (!silent) Console.WriteLine($"  [4] Handle duplicate ......... FAILED ({ex.Message})");
    }

    // Strategy 5: Restart Manager (shuts down locking app, copies, restarts it)
    try
    {
        if (!silent)
        {
            Console.WriteLine("  [5] Restart Manager .......... This will shut down the locking app to copy the file.");
            Console.Write("  [5] Restart Manager .......... Continue? (y/n): ");
            var key = Console.ReadLine();
            if (!string.Equals(key?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  [5] Restart Manager .......... SKIPPED (user declined)");
                return false;
            }
            Console.WriteLine("  [5] Restart Manager .......... RUNNING...");
        }

        bool progressStarted5 = false;
        long totalSize5 = new FileInfo(inputPath).Length;
        Timer? timer5 = null;
        if (!silent)
        {
            timer5 = new Timer(_ =>
            {
                if (!progressStarted5)
                {
                    progressStarted5 = true;
                    Console.WriteLine();
                }
                Console.Write($"\r  [5] Restart Manager .......... copying...   ");
            }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        }

        try
        {
            RestartManager.CopyWithRestart(inputPath, outputPath, !silent ? (copied, total) =>
            {
                if (!progressStarted5)
                {
                    progressStarted5 = true;
                    Console.WriteLine();
                }
                double pct = total > 0 ? (double)copied / total * 100.0 : 0;
                Console.Write($"\r  [5] Restart Manager .......... {pct:F1}% | {copied:N0} / {total:N0} bytes   ");
            } : null);
        }
        finally
        {
            timer5?.Dispose();
        }

        if (!silent) Console.Write($"\r{new string(' ', 80)}\r");
        if (!silent) Console.WriteLine("  [5] Restart Manager .......... SUCCESS");
        if (!silent) PrintResult(outputPath);
        return true;
    }
    catch (Exception ex)
    {
        if (!silent) Console.WriteLine($"  [5] Restart Manager .......... FAILED ({ex.Message})");
    }

    // All strategies failed
    if (!silent)
    {
        Console.WriteLine();
        Console.WriteLine("RESULT: All strategies failed.");
        var processes = GetProcessesLockingFile(inputPath);
        if (processes.Count > 0)
        {
            Console.WriteLine("Locked by:");
            foreach (var (name, pid) in processes)
                Console.WriteLine($"  - {name} (PID: {pid})");
        }
        Console.WriteLine("Close the application that has this file open and try again.");
    }
    return false;
}

static void PrintResult(string outputPath)
{
    var copiedSize = new FileInfo(outputPath).Length;
    Console.WriteLine();
    Console.WriteLine($"Copied successfully: {outputPath} ({copiedSize:N0} bytes)");
}

static List<(string Name, int Pid)> GetProcessesLockingFile(string filePath)
{
    var result = new List<(string, int)>();
    var normalizedPath = Path.GetFullPath(filePath).ToLowerInvariant();

    foreach (var proc in Process.GetProcesses())
    {
        try
        {
            if (proc.Id == 0 || proc.Id == 4) continue;
            foreach (var module in proc.Modules)
            {
                var mod = (ProcessModule)module;
                if (mod.FileName.ToLowerInvariant() == normalizedPath)
                {
                    result.Add((proc.ProcessName, proc.Id));
                    break;
                }
            }
        }
        catch { }
    }
    return result;
}