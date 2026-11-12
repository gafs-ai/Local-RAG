using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LocalRag.Infrastructure;

/// <summary>Read hardware descriptions only; preserve NPU driver names without guessing a chip model.</summary>
public static class UsageStatisticsHardwareDetector
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AMD Ryzen AI 5 PRO 340 w/ Radeon 840M"] = "AMD Ryzen AI 5 PRO 340",
        ["AMD Ryzen AI 5 PRO 340 w/ Radeon 840M Graphics"] = "AMD Ryzen AI 5 PRO 340",
        ["AMD Ryzen AI 9 HX 370 w/ Radeon 890M"] = "AMD Ryzen AI 9 HX 370",
        ["AMD Ryzen AI 9 HX 375 w/ Radeon 890M"] = "AMD Ryzen AI 9 HX 375",
        ["AMD Ryzen AI 9 365 w/ Radeon 880M"] = "AMD Ryzen AI 9 365",
        ["AMD Ryzen 7 7840U w/ Radeon 780M Graphics"] = "AMD Ryzen 7 7840U",
        ["AMD Ryzen 7 8840U w/ Radeon 780M Graphics"] = "AMD Ryzen 7 8840U",
        ["AMD Ryzen 7 8845HS w/ Radeon 780M Graphics"] = "AMD Ryzen 7 8845HS",
        ["Intel(R) Core(TM) Ultra 7 258V"] = "Intel Core Ultra 7 258V",
        ["Intel(R) Core(TM) Ultra 7 155H"] = "Intel Core Ultra 7 155H",
        ["Intel(R) Core(TM) Ultra 5 125H"] = "Intel Core Ultra 5 125H",
        ["13th Gen Intel(R) Core(TM) i7-13700"] = "Intel Core i7-13700",
        ["13th Gen Intel(R) Core(TM) i5-13400"] = "Intel Core i5-13400",
        ["AMD Radeon(TM) 890M Graphics"] = "AMD Radeon 890M",
        ["AMD Radeon(TM) 880M Graphics"] = "AMD Radeon 880M",
        ["AMD Radeon(TM) 780M Graphics"] = "AMD Radeon 780M",
        ["AMD Radeon(TM) 760M Graphics"] = "AMD Radeon 760M",
        ["AMD Radeon(TM) 840M Graphics"] = "AMD Radeon 840M",
        ["AMD Radeon 890M Graphics"] = "AMD Radeon 890M",
        ["AMD Radeon 880M Graphics"] = "AMD Radeon 880M",
        ["AMD Radeon 780M Graphics"] = "AMD Radeon 780M",
        ["AMD Radeon 760M Graphics"] = "AMD Radeon 760M",
        ["AMD Radeon 840M Graphics"] = "AMD Radeon 840M",
        ["Intel(R) Arc(TM) 140V GPU"] = "Intel Arc 140V",
        ["Intel(R) Arc(TM) 130V GPU"] = "Intel Arc 130V",
        ["Intel(R) Arc(TM) Graphics"] = "Intel Arc Graphics",
        ["Intel(R) Iris(R) Xe Graphics"] = "Intel Iris Xe Graphics"
    };

    private const string NpuNamePattern = @"\b(?:NPU|IPU)\b|Neural|AI\s+Boost|XDNA";
    // PNPClass is used only for candidate filtering; only Name is retained. Never read IDs or serials.
    private static readonly string Probe = """
        $ErrorActionPreference = 'SilentlyContinue'
        $ProgressPreference = 'SilentlyContinue'
        $OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        function EmitNames($kind, $items) {
            @($items) | Select-Object -First 8 | ForEach-Object {
                $value = [string]$_.Name
                if ($value.Length -le 160) { @{kind=$kind;name=$value} | ConvertTo-Json -Compress }
            }
        }
        EmitNames 'gpu' (Get-CimInstance -Query 'SELECT Name FROM Win32_VideoController')
        try {
            $devices = @(Get-CimInstance -ErrorAction Stop -Query "SELECT Name, PNPClass FROM Win32_PnPEntity WHERE PNPClass = 'ComputeAccelerator' OR Name LIKE '%Neural%' OR Name LIKE '%AI Boost%' OR Name LIKE '%IPU%' OR Name LIKE '%NPU%' OR Name LIKE '%XDNA%'" |
                Where-Object { $_.PNPClass -eq 'ComputeAccelerator' -or $_.Name -match '@@NPU_PATTERN@@' })
            $devices | Select-Object -First 8 | ForEach-Object {
                $value = [string]$_.Name
                if ($value.Length -le 160) { @{kind='npu';name=$value;class=[string]$_.PNPClass} | ConvertTo-Json -Compress }
            }
            @{kind='npu-complete';count=$devices.Count} | ConvertTo-Json -Compress
        } catch { }
        """.Replace("@@NPU_PATTERN@@", NpuNamePattern, StringComparison.Ordinal);

    public static async Task<UsageStatisticsHardwareInput> DetectAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return new();
        string? cpu = null;
        long memoryBytes = 0;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpu = key?.GetValue("ProcessorNameString") as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory)) memoryBytes = (long)Math.Min(memory.TotalPhysical, (ulong)long.MaxValue);
        var gpu = new List<string>();
        var npu = new List<string>();
        int? npuCandidateCount = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        // CIM may initialize slowly on a busy PC. Detection runs in the background,
        // so allow both queries to finish while still bounding the probe lifetime.
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Probe)) })
                start.ArgumentList.Add(argument);
            process = Process.Start(start);
            if (process is not null)
            {
                var errors = DiscardAsync(process.StandardError, timeout.Token);
                await ReadDeviceOutputAsync(process.StandardOutput, gpu, npu, timeout.Token,
                    count => npuCandidateCount = count).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await errors.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                process.Dispose();
            }
        }
        token.ThrowIfCancellationRequested();
        return Normalize(cpu, gpu, npu, memoryBytes, npuCandidateCount);
    }

    internal static async Task ReadDeviceOutputAsync(TextReader reader, List<string> gpu, List<string> npu, CancellationToken token,
        Action<int>? npuCompleted = null)
    {
        var buffer = new char[16384];
        var count = 0;
        try
        {
            while (count < buffer.Length)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
        }
        finally
        {
            // A slow NPU query must not discard already completed GPU records. A partial
            // trailing JSON line is ignored. A successful empty query differs from a failed query.
            ReadNames(new string(buffer, 0, count), gpu, npu, npuCompleted);
        }
    }

    private static async Task DiscardAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1024];
        try { while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) != 0) { } }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static void ReadNames(string output, List<string> gpu, List<string> npu, Action<int>? npuCompleted)
    {
        foreach (var line in output.Split('\n').Take(17))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                if (!json.RootElement.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) continue;
                if (kind.GetString() == "npu-complete")
                {
                    if (json.RootElement.TryGetProperty("count", out var count) && count.ValueKind == JsonValueKind.Number &&
                        count.TryGetInt32(out var total) && total >= 0)
                        npuCompleted?.Invoke(total);
                    continue;
                }
                if (!json.RootElement.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
                var value = name.GetString();
                if (value is null || value.Length > 160) continue;
                if (kind.GetString() == "gpu") gpu.Add(value);
                else if (kind.GetString() == "npu")
                {
                    var deviceClass = json.RootElement.TryGetProperty("class", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
                    if (IsNpuCandidate(value, deviceClass)) npu.Add(value);
                }
            }
            catch (JsonException) { }
        }
    }

    internal static bool IsNpuCandidate(string? name, string? deviceClass) =>
        string.Equals(deviceClass, "ComputeAccelerator", StringComparison.OrdinalIgnoreCase) ||
        name is not null && Regex.IsMatch(name, NpuNamePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static UsageStatisticsHardwareInput Normalize(string? cpu, IEnumerable<string> gpus, IEnumerable<string> npus, long memoryBytes,
        int? npuCandidateCount = null)
    {
        static string? Alias(string? value) => value is null ? null : Aliases.GetValueOrDefault(value.Trim(), value.Trim());
        var clean = UsageStatisticsPrivacy.Hardware(new(Alias(cpu), MemoryBytes: memoryBytes));
        var gpu = gpus.Take(8).Select(name => UsageStatisticsPrivacy.Hardware(new(Gpu: Alias(name))).Gpu)
            .FirstOrDefault(name => name != "unknown") ?? "unknown";
        var npu = npus.Take(8).Select(UsageStatisticsPrivacy.NpuName)
            .FirstOrDefault(name => name != "unknown") ?? (npuCandidateCount == 0 ? "なし" : "unknown");
        return new(clean.Cpu, gpu, npu, (long)clean.MemoryGiB * 1024 * 1024 * 1024);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
