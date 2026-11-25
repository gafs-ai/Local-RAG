using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LocalRag.Infrastructure;

internal static partial class WindowsServerProcess
{
    // This temporary server has no interactive owner. If the desktop crashes, its DB lock
    // must be released before startup recovery removes the unfinished replacement.
    internal static (Process Process, IDisposable Lifetime) StartForMaintenance(string executable, IReadOnlyList<string> args, string password)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        try
        {
            var limits = new ExtendedJobLimits { Basic = new() { Flags = 0x2000 /* KILL_ON_JOB_CLOSE */ } };
            if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedJobLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DB整理用プロセスの監視を準備できませんでした。");
            var process = StartCore(executable, args,
                new Dictionary<string, string> { ["SURREAL_PASS"] = password, ["SURREAL_TELEMETRY_PROVIDER"] = "none" }, "SURREAL_", job);
            return (process, job);
        }
        catch { job.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicJobLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedJobLimits
    {
        public BasicJobLimits Basic;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedJobLimits limits, uint bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
