using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using LocalRag.Infrastructure;

namespace LocalRag.Desktop;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _openManager;
    private RegisteredWaitHandle? _activationWait;
    internal bool IsExiting { get; private set; }
    private bool _exitRequested;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = DesktopLaunchOptions.Parse(e.Args, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalRAG"));
        if (options is null) { Shutdown(1); return; }
        if (options.StartInTray)
        {
            try
            {
                if (WindowsLogonStartup.ForCurrentInstallation(options.DataDirectory)?.IsOptedOut == true) { Shutdown(); return; }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                // An unreadable preference must not override an opt-out during unattended sign-in.
                Shutdown(1);
                return;
            }
        }
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.DataDirectory.ToUpperInvariant())));
        // Create the signal before claiming the mutex so a manual launch racing sign-in is remembered.
        _openManager = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LocalRag-Open-" + key);
        _mutex = new Mutex(true, @"Local\LocalRag-" + key, out _ownsMutex);
        if (!_ownsMutex)
        {
            if (!options.StartInTray) _openManager.Set();
            Shutdown();
            return;
        }
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (System.ComponentModel.Win32Exception) { /* Priority is advisory. */ }
        var window = new MainWindow(options.DataDirectory);
        MainWindow = window;
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_openManager, (_, _) =>
            Dispatcher.InvokeAsync(() => { if (!_exitRequested) window.ShowManager(); }), null, Timeout.Infinite, false);
        if (options.StartInTray) await window.InitializeOnceAsync();
        else window.Show();
    }

    internal async void ExitApplication()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        try
        {
            if (MainWindow is MainWindow window) await window.PrepareShutdownAsync();
            IsExiting = true;
            Shutdown();
        }
        catch (Exception ex)
        {
            _exitRequested = false;
            MessageBox.Show($"サーバーの終了を確認できませんでした（{ex.GetType().Name}）。アプリは開いたままです。Lemonade・DBの状態を確認し、少し待って再度終了してください。必要なら対象サーバーを残す設定を保存して終了できます。", "Local RAG");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationWait?.Unregister(null);
        _openManager?.Dispose();
        if (MainWindow is MainWindow window) window.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
