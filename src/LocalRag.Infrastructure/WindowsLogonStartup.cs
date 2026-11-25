using Microsoft.Win32;

namespace LocalRag.Infrastructure;

/// <summary>Per-user registration shared by the MSI and the Windows settings page.</summary>
public sealed class WindowsLogonStartup
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string Preferences = @"Software\GAFS\LocalRAG\Preferences";
    internal const string OptOutName = "StartAtLogonDisabled";
    internal const string RunName = "LocalRAG";
    private const string ExeName = "LocalRag.Desktop.exe";
    private const string Arguments = "\" --startup-tray";
    private readonly ILogonRegistry _registry;
    private readonly string _command;
    private readonly bool _portable;

    private WindowsLogonStartup(ILogonRegistry registry, string executable, bool portable)
    {
        _registry = registry;
        _command = "\"" + executable + Arguments;
        _portable = portable;
    }

    public static WindowsLogonStartup? ForCurrentInstallation(string dataDirectory) =>
        Find(dataDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalRAG"),
            Environment.ProcessPath, new CurrentUserRegistry());

    internal static WindowsLogonStartup? Find(string dataDirectory, string defaultDirectory, string? executable, ILogonRegistry registry)
    {
        if (!SamePath(dataDirectory, defaultDirectory) || executable is null ||
            !string.Equals(Path.GetFileName(executable), ExeName, StringComparison.OrdinalIgnoreCase)) return null;
        var directory = Path.GetDirectoryName(executable)!;
        var testInstall = registry.Read(@"Software\GAFS\LocalRAG-Test\Installer", "InstallDirectory") as string;
        if (testInstall is not null && SamePath(directory, testInstall)) return null;
        var install = registry.Read(@"Software\GAFS\LocalRAG\Installer", "InstallDirectory") as string;
        if (install is not null)
            return SamePath(directory, install) ? new(registry, executable, false) : null;
        // Package.ps1 includes this marker in the ZIP. A developer build must not register its output directory.
        return File.Exists(Path.Combine(directory, "build-info.json")) ? new(registry, executable, true) : null;
    }

    private static bool SamePath(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    public bool IsOptedOut => _registry.Read(Preferences, OptOutName) is 1;
    public bool Enabled => !IsOptedOut && EqualsCommand(_registry.Read(RunKey, RunName));
    private bool EqualsCommand(object? command) => command is string s && string.Equals(s, _command, StringComparison.OrdinalIgnoreCase);

    public void RegisterPortableDefault()
    {
        if (!_portable || IsOptedOut) return;
        var entry = _registry.Read(RunKey, RunName);
        if (entry is null || IsMissingPreviousCopy(entry)) SetEnabled(true);
    }

    private static bool IsMissingPreviousCopy(object? entry)
    {
        if (entry is not string command || command.Length <= Arguments.Length + 1 ||
            !command.StartsWith('"') || !command.EndsWith(Arguments, StringComparison.OrdinalIgnoreCase)) return false;
        var path = command[1..^Arguments.Length];
        return Path.IsPathFullyQualified(path) && !path.Contains('"') &&
            string.Equals(Path.GetFileName(path), ExeName, StringComparison.OrdinalIgnoreCase) && !File.Exists(path);
    }

    public void SetEnabled(bool enabled)
    {
        var entry = _registry.Read(RunKey, RunName);
        if (entry is not null && !EqualsCommand(entry) && !IsMissingPreviousCopy(entry))
            throw new InvalidOperationException("別の場所にあるLocal RAGがスタートアップに登録されています。登録したアプリで設定を変更してください。");
        if (enabled)
        {
            if (_command.Length > 260) throw new InvalidOperationException("Windowsのスタートアップ登録に使用できる長さを超えています。短いパスに配置してください。");
            _registry.Write(RunKey, RunName, _command);
            _registry.Write(Preferences, OptOutName, null);
        }
        else
        {
            // A stale launch already dispatched by Windows must see the opt-out before initializing.
            _registry.Write(Preferences, OptOutName, 1);
            _registry.Write(RunKey, RunName, null);
        }
    }

    internal interface ILogonRegistry
    {
        object? Read(string path, string name);
        void Write(string path, string name, object? value);
    }

    private sealed class CurrentUserRegistry : ILogonRegistry
    {
        public object? Read(string path, string name)
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        public void Write(string path, string name, object? value)
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            if (value is null)
            {
                using var existing = hive.OpenSubKey(path, writable: true);
                existing?.DeleteValue(name, throwOnMissingValue: false);
                return;
            }
            using var key = hive.CreateSubKey(path);
            key.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        }
    }
}
