using System.Diagnostics;
using System.Runtime.InteropServices;

// This helper owns no DB data. It only sends one graceful signal to a verified private server console.
if (args.Length != 2 || !int.TryParse(args[0], out var pid) || !long.TryParse(args[1], out var startTicks)) return 1;
try
{
    using var process = Process.GetProcessById(pid);
    if (process.StartTime.ToUniversalTime().Ticks != startTicks || process.HasExited) return 2;
    Native.FreeConsole();
    if (!Native.AttachConsole((uint)pid)) return 3;
    try
    {
        Native.Handler handler = _ => true;
        if (!Native.SetConsoleCtrlHandler(handler, true)) return 4;
        var members = new uint[8];
        var count = Native.GetConsoleProcessList(members, (uint)members.Length);
        if (count == 0 || count > members.Length || members.Take((int)count).Any(x => x != (uint)pid && x != (uint)Environment.ProcessId))
            return 5;
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startTicks) return 2;
        var sent = Native.GenerateConsoleCtrlEvent(1, 0); // CTRL_BREAK; isolated console only.
        // Wait for the queued signal while our ignore handler is still installed. FreeConsole resets handlers.
        if (sent) process.WaitForExit(30000);
        GC.KeepAlive(handler);
        return sent ? 0 : 6;
    }
    finally { Native.FreeConsole(); }
}
catch { return 7; }

internal static class Native
{
    internal delegate bool Handler(uint signal);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetConsoleCtrlHandler(Handler handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetConsoleProcessList([Out] uint[] processes, uint count);
}
