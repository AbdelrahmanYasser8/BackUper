using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BackUper.Services;

internal static class RestartManager
{
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    internal static extern int RmStartSession(out uint sessionHandle, int flags, string sessionKey);

    [DllImport("rstrtmgr.dll")]
    internal static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    internal static extern int RmRegisterResources(uint sessionHandle,
        uint nFiles, string[] rgsFileNames,
        uint nApplications, IntPtr rgApplications,
        uint nServices, IntPtr rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    internal static extern int RmShutdown(uint sessionHandle, uint actionFlags, IntPtr fnStatus);

    [DllImport("rstrtmgr.dll")]
    internal static extern int RmRestart(uint sessionHandle, int restartFlags, IntPtr fnStatus);

    internal const uint RmForceShutdown = 0x00000001;

    internal static void CopyWithRestart(string inputPath, string outputPath, Action<long, long>? onProgress = null)
    {
        var sessionKey = Guid.NewGuid().ToString();
        int result = RmStartSession(out uint handle, 0, sessionKey);
        if (result != 0) throw new Exception($"RmStartSession failed: {result}");

        try
        {
            var files = new[] { inputPath };
            result = RmRegisterResources(handle, (uint)files.Length, files, 0, IntPtr.Zero, 0, IntPtr.Zero);
            if (result != 0) throw new Exception($"RmRegisterResources failed: {result}");

            result = RmShutdown(handle, RmForceShutdown, IntPtr.Zero);
            if (result != 0) throw new Exception($"RmShutdown failed: {result}");

            using var src = new FileStream(inputPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 16 * 1024 * 1024, FileOptions.SequentialScan);
            using var dst = new FileStream(outputPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 16 * 1024 * 1024, FileOptions.SequentialScan);

            long totalSize = src.Length;
            long totalCopied = 0;
            byte[] buffer = new byte[16 * 1024 * 1024];
            int bytesRead;
            while ((bytesRead = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                dst.Write(buffer, 0, bytesRead);
                totalCopied += bytesRead;
                onProgress?.Invoke(totalCopied, totalSize);
            }

            result = RmRestart(handle, 0, IntPtr.Zero);
            if (result != 0) throw new Exception($"RmRestart failed: {result}");
        }
        finally
        {
            RmEndSession(handle);
        }
    }
}