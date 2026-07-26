using System.Diagnostics;
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
        File.Copy(inputPath, outputPath, overwrite: true);
        if (!silent) Console.WriteLine("  [1] Normal copy .............. SUCCESS");
        if (!silent) PrintResult(outputPath);
        return true;
    }
    catch (IOException) { if (!silent) Console.WriteLine("  [1] Normal copy .............. SKIPPED (file locked)"); }

    // Strategy 2: Shared-access FileStream
    try
    {
        using var src = new FileStream(inputPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete | FileShare.Write);
        using var dst = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
        if (!silent) Console.WriteLine("  [2] Shared-access copy ....... SUCCESS");
        if (!silent) PrintResult(outputPath);
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
        process.WaitForExit(60000);
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
        if (HandleDuplicateCopy.TryCopy(inputPath, outputPath))
        {
            if (!silent) Console.WriteLine("  [4] Handle duplicate ......... SUCCESS");
            if (!silent) PrintResult(outputPath);
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
        if (!silent) Console.WriteLine("  [5] Restart Manager .......... RUNNING (may close app)...");
        RestartManager.CopyWithRestart(inputPath, outputPath);
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