using System.Runtime.InteropServices;

namespace DataRepo.Catalog;

/// <summary>Which processes hold a file open, as Windows' Restart Manager reports them (aging DATAREPO-81 b).</summary>
/// <remarks>Diagnostic only: it names the holder in a refusal and never decides anything, so any failure to ask gives
/// an empty answer rather than an error.</remarks>
internal static class FileHolders
{
    /// <summary>Each process holding any of <paramref name="paths"/> open, as <c>name (pid N)</c>. Empty off Windows, when
    /// nothing holds them, or when Windows cannot say.</summary>
    public static IReadOnlyList<string> Of(params string[] paths)
    {
        if (!OperatingSystem.IsWindows() || paths.Length == 0) return [];
        try
        {
            return Query(paths);
        }
        catch (Exception)
        {
            // rstrtmgr.dll missing or a marshalling failure: the refusal is still right, it just cannot name anyone
            return [];
        }
    }

    private static List<string> Query(string[] paths)
    {
        // The session key is written by Windows: CCH_RM_SESSION_KEY (32) characters and a terminator.
        var key = new System.Text.StringBuilder(33);
        if (RmStartSession(out var session, 0, key) != 0) return [];
        try
        {
            if (RmRegisterResources(session, (uint)paths.Length, paths, 0, null, 0, null) != 0) return [];
            uint count = 0, reasons = 0;
            var rc = RmGetList(session, out var needed, ref count, null, ref reasons);
            if (rc != ErrorMoreData) return []; // 0: nothing holds them
            var infos = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out _, ref count, infos, ref reasons) != 0) return [];
            return infos.Take((int)count).Select(i => $"{i.AppName} (pid {i.Process.ProcessId})").ToList();
        }
        finally
        {
            RmEndSession(session);
        }
    }

    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, System.Text.StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint sessionHandle, uint fileCount, string[] fileNames, uint applicationCount,
        RmUniqueProcess[]? applications, uint serviceCount, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint sessionHandle, out uint processInfoNeeded, ref uint processInfo,
        [In, Out] RmProcessInfo[]? affectedApps, ref uint rebootReasons);
}
