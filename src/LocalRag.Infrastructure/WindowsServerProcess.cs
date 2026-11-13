using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LocalRag.Infrastructure;

internal static class WindowsServerProcess
{
    // A private hidden console is essential: a stop signal must never reach the app's terminal or other servers.
    public static Process Start(string executable, IReadOnlyList<string> arguments, string password)
        => StartIsolated(executable, arguments, new Dictionary<string, string> { ["SURREAL_PASS"] = password, ["SURREAL_TELEMETRY_PROVIDER"] = "none" }, "SURREAL_");

    internal static Process StartIsolated(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? overrides = null, string? removePrefix = null)
    {
        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
            if (removePrefix is null || !((string)item.Key).StartsWith(removePrefix, StringComparison.OrdinalIgnoreCase))
                environment[(string)item.Key] = (string?)item.Value ?? "";
        if (overrides is not null) foreach (var item in overrides) environment[item.Key] = item.Value;
        var block = string.Join('\0', environment.Select(x => $"{x.Key}={x.Value}")) + "\0\0";
        var memory = Marshal.StringToHGlobalUni(block);
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 1, ShowWindow = 0 };
        var command = new StringBuilder(string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteArgument)));
        try
        {
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                0x00000010 | 0x00000400 | 0x00004000, memory, Path.GetDirectoryName(executable), ref startup, out var information))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "サーバーを起動できませんでした。");
            try { return Process.GetProcessById((int)information.ProcessId); }
            finally { CloseHandle(information.Thread); CloseHandle(information.Process); }
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(memory); }
    }

    internal static string QuoteArgument(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') result.Append('\\', slashes * 2 + 1).Append(ch);
            else result.Append('\\', slashes).Append(ch);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
