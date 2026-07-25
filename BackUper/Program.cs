using System.Diagnostics;
using BackUper.Services;

if (args.Length < 3)
{
    Console.WriteLine("Usage: BackUper copy <input> <output>");
    return;
}

var inputPath = Path.GetFullPath(args[1]);
var outputPath = Path.GetFullPath(args[2]);

if (!File.Exists(inputPath))
{
    Console.WriteLine($"Error: Input file not found: {inputPath}");
    return;
}

CopyFileWithFallback(inputPath, outputPath);

static void CopyFileWithFallback(string inputPath, string outputPath)
{
    var dir = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        Directory.CreateDirectory(dir);

    var totalSize = new FileInfo(inputPath).Length;
    Console.WriteLine($"Source: {inputPath} ({totalSize:N0} bytes)");
    Console.WriteLine($"Target: {outputPath}");
    Console.WriteLine();
    Console.WriteLine("Strategies tried:");

    // Strategy 1: Normal copy
    try
    {
        File.Copy(inputPath, outputPath, overwrite: true);
        Console.WriteLine("  [1] Normal copy .............. SUCCESS");
        PrintResult(outputPath);
        return;
    }
    catch (IOException) { Console.WriteLine("  [1] Normal copy .............. SKIPPED (file locked)"); }

    // Strategy 2: Shared-access FileStream
    try
    {
        using var src = new FileStream(inputPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete | FileShare.Write);
        using var dst = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
        Console.WriteLine("  [2] Shared-access copy ....... SUCCESS");
        PrintResult(outputPath);
        return;
    }
    catch (IOException) { Console.WriteLine("  [2] Shared-access copy ....... SKIPPED (exclusive lock)"); }

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
            Console.WriteLine("  [3] esentutl copy ............ SUCCESS");
            PrintResult(outputPath);
            return;
        }
        Console.WriteLine("  [3] esentutl copy ............ FAILED (exit code " + process.ExitCode + ")");
    }
    catch { Console.WriteLine("  [3] esentutl copy ............ SKIPPED (not available)"); }

    // Strategy 4: Duplicate handle (no admin, no app close)
    try
    {
        if (HandleDuplicateCopy.TryCopy(inputPath, outputPath))
        {
            Console.WriteLine("  [4] Handle duplicate ......... SUCCESS");
            PrintResult(outputPath);
            return;
        }
        Console.WriteLine("  [4] Handle duplicate ......... FAILED (no matching handle found)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [4] Handle duplicate ......... FAILED ({ex.Message})");
    }

    // Strategy 5: Restart Manager (shuts down locking app, copies, restarts it)
    try
    {
        Console.WriteLine("  [5] Restart Manager .......... RUNNING (may close app)...");
        RestartManager.CopyWithRestart(inputPath, outputPath);
        Console.WriteLine("  [5] Restart Manager .......... SUCCESS");
        PrintResult(outputPath);
        return;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [5] Restart Manager .......... FAILED ({ex.Message})");
    }

    // All strategies failed
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