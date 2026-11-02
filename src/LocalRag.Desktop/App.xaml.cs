using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace LocalRag.Desktop;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    internal bool IsExiting { get; private set; }
    private bool _exitRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalRAG");
        if (e.Args.Length == 2 && e.Args[0] == "--data-dir") directory = Path.GetFullPath(e.Args[1]);
        else if (e.Args.Length != 0) { Shutdown(1); return; }
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())));
        _mutex = new Mutex(true, @"Local\LocalRag-" + key, out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("Local RAGはすでに起動しています。タスクトレイから管理画面を開いてください。", "Local RAG");
            Shutdown();
            return;
        }
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (System.ComponentModel.Win32Exception) { /* Priority is advisory. */ }
        var window = new MainWindow(directory);
        MainWindow = window;
        window.Show();
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
            MessageBox.Show($"DBの終了を確認できませんでした（{ex.GetType().Name}）。アプリは開いたままです。少し待って再度終了するか、DBを残す設定を保存して終了してください。", "Local RAG");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow is MainWindow window) window.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
