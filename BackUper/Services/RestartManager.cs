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

    internal static void CopyWithRestart(string inputPath, string outputPath)
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

            File.Copy(inputPath, outputPath, overwrite: true);

            result = RmRestart(handle, 0, IntPtr.Zero);
            if (result != 0) throw new Exception($"RmRestart failed: {result}");
        }
        finally
        {
            RmEndSession(handle);
        }
    }
}