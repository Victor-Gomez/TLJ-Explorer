using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TLJExplorer.Services;

/// <summary>
/// Makes sure child processes (e.g. <c>ffmpeg</c>) don't outlive this app.
/// </summary>
/// <remarks>
/// On Windows, processes are assigned to a Job Object flagged with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>:
/// when this app's process handle is closed -- whether via a clean exit, an unhandled exception, or Task
/// Manager -- the kernel automatically terminates every process still in the job. There's no equivalent
/// kernel primitive on Linux/macOS, so elsewhere we just track the processes ourselves and kill them on
/// <see cref="AppDomain.ProcessExit"/>; ffmpeg invocations here are short-lived (transcode-then-exit)
/// rather than long-running daemons, so "best-effort kill on our own exit" covers the same crash/Task
/// Manager cases the Job Object does on Windows.
/// </remarks>
public static class ChildProcessTracker
{
    private static readonly IntPtr JobHandle = OperatingSystem.IsWindows() ? CreateAndConfigureJob() : IntPtr.Zero;
    private static readonly object FallbackGate = new();
    private static readonly List<Process> FallbackTracked = [];

    static ChildProcessTracker()
    {
        if (!OperatingSystem.IsWindows())
            AppDomain.CurrentDomain.ProcessExit += (_, _) => KillFallbackTracked();
    }

    /// <summary>Adds a running <paramref name="process"/> to the kill-on-close job (or the fallback tracked list).</summary>
    public static void AddProcess(Process process)
    {
        if (process.HasExited)
            return;

        if (OperatingSystem.IsWindows())
        {
            if (JobHandle == IntPtr.Zero)
                return;
            try
            {
                AssignProcessToJobObject(JobHandle, process.Handle);
            }
            catch
            {
                // Best-effort; on failure the process simply won't get auto-killed.
            }
            return;
        }

        lock (FallbackGate)
            FallbackTracked.Add(process);
    }

    private static void KillFallbackTracked()
    {
        lock (FallbackGate)
        {
            foreach (Process p in FallbackTracked)
            {
                try
                {
                    if (!p.HasExited)
                        p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort on shutdown.
                }
            }
            FallbackTracked.Clear();
        }
    }

    // -------------------------------------------------------------------------------------------
    // Win32 interop
    // -------------------------------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static IntPtr CreateAndConfigureJob()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return IntPtr.Zero;

        var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE };
        var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = info };

        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(extended, ptr, false);
            SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)length);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        return job;
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
