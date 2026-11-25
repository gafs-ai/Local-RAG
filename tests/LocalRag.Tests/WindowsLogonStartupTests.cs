using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class WindowsLogonStartupTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory();
    private readonly MemoryRegistry _registry = new();
    private const string Installer = @"Software\GAFS\LocalRAG\Installer";
    private const string TestInstaller = @"Software\GAFS\LocalRAG-Test\Installer";
    private string Profile => Path.Combine(_root.FullName, "profile");
    private string DirectoryFor(string name) => Directory.CreateDirectory(Path.Combine(_root.FullName, name)).FullName;
    private WindowsLogonStartup? Open(string directory) => WindowsLogonStartup.Find(Profile, Profile,
        Path.Combine(directory, "LocalRag.Desktop.exe"), _registry);
    private object? Command => _registry.Read(WindowsLogonStartup.RunKey, WindowsLogonStartup.RunName);

    [Fact]
    public void PortableRegistersQuotedCommandOnceAndHonorsSavedOptOut()
    {
        var dir = DirectoryFor("展開 先");
        File.WriteAllText(Path.Combine(dir, "LocalRag.Desktop.exe"), "fixture");
        File.WriteAllText(Path.Combine(dir, "build-info.json"), "{}");
        var startup = Assert.IsType<WindowsLogonStartup>(Open(dir));
        startup.RegisterPortableDefault();
        Assert.Equal($"\"{Path.Combine(dir, "LocalRag.Desktop.exe")}\" --startup-tray", Command);
        Assert.True(startup.Enabled);
        var writes = _registry.Writes;
        startup.RegisterPortableDefault();
        Assert.Equal(writes, _registry.Writes);
        startup.SetEnabled(false);
        Assert.Null(Command);
        var reopened = Assert.IsType<WindowsLogonStartup>(Open(dir));
        reopened.RegisterPortableDefault();
        Assert.True(reopened.IsOptedOut);
        Assert.False(reopened.Enabled);
        reopened.SetEnabled(true);
        Assert.True(reopened.Enabled);
        Assert.False(reopened.IsOptedOut);
    }

    [Fact]
    public void InstallerOwnsInitialRegistrationAndOtherCopiesCannotReplaceIt()
    {
        var dir = DirectoryFor("MSI");
        _registry.Write(Installer, "InstallDirectory", dir + Path.DirectorySeparatorChar);
        var installed = Assert.IsType<WindowsLogonStartup>(Open(dir));
        var writes = _registry.Writes;
        installed.RegisterPortableDefault();
        Assert.Equal(writes, _registry.Writes);
        installed.SetEnabled(true);
        var other = DirectoryFor("ZIP");
        File.WriteAllText(Path.Combine(other, "build-info.json"), "{}");
        Assert.Null(Open(other));
        _registry.Write(WindowsLogonStartup.RunKey, WindowsLogonStartup.RunName, "another-program.exe");
        Assert.Throws<InvalidOperationException>(() => installed.SetEnabled(false));
        Assert.Equal("another-program.exe", Command);
    }

    [Fact]
    public void IsolatedProfilesTestPackagesAndDevelopmentHostsDoNotRegister()
    {
        var dir = DirectoryFor("build");
        Assert.Null(WindowsLogonStartup.Find(Profile + "-isolated", Profile, Path.Combine(dir, "LocalRag.Desktop.exe"), _registry));
        Assert.Null(WindowsLogonStartup.Find(Profile, Profile, Path.Combine(dir, "dotnet.exe"), _registry));
        Assert.Equal(0, _registry.Reads);
        Assert.Null(Open(dir)); // Unpackaged build.
        File.WriteAllText(Path.Combine(dir, "build-info.json"), "{}");
        _registry.Write(TestInstaller, "InstallDirectory", dir);
        Assert.Null(Open(dir));
        Assert.Null(Command);
    }

    [Fact]
    public void MovedZipUpdatesAStaleTargetAndLeavesALiveTargetAlone()
    {
        var dir = DirectoryFor("ZIP");
        File.WriteAllText(Path.Combine(dir, "build-info.json"), "{}");
        var previous = Path.Combine(DirectoryFor("old"), "LocalRag.Desktop.exe");
        var oldCommand = $"\"{previous}\" --startup-tray";
        File.WriteAllText(previous, "fixture");
        _registry.Write(WindowsLogonStartup.RunKey, WindowsLogonStartup.RunName, oldCommand);
        var zip = Assert.IsType<WindowsLogonStartup>(Open(dir));
        zip.RegisterPortableDefault();
        Assert.Equal(oldCommand, Command);
        File.Delete(previous);
        zip.RegisterPortableDefault();
        Assert.True(zip.Enabled);
    }

    public void Dispose() => _root.Delete(true);

    private sealed class MemoryRegistry : WindowsLogonStartup.ILogonRegistry
    {
        private readonly Dictionary<(string, string), object> _values = [];
        internal int Writes { get; private set; }
        internal int Reads { get; private set; }
        public object? Read(string path, string name) { Reads++; return _values.GetValueOrDefault((path, name)); }
        public void Write(string path, string name, object? value)
        {
            Writes++;
            if (value is null) _values.Remove((path, name));
            else _values[(path, name)] = value;
        }
    }
}
