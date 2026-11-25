using System.Windows;
using LocalRag.Infrastructure;

namespace LocalRag.Desktop;

public partial class MainWindow
{
    private WindowsLogonStartup? _logonStartup;

    private void InitializeLogonSetting()
    {
        try
        {
            _logonStartup = WindowsLogonStartup.ForCurrentInstallation(_dataDirectory);
            if (_logonStartup is null) return;
            StartAtLogonBox.Visibility = Visibility.Visible;
            if (IsVisible) _logonStartup.RegisterPortableDefault();
            StartAtLogonBox.IsChecked = _logonStartup.Enabled;
        }
        catch (Exception ex) { ShowLogonSettingError(ex); }
    }

    private void StartAtLogonClick(object sender, RoutedEventArgs e)
    {
        if (_logonStartup is null) return;
        try
        {
            _logonStartup.SetEnabled(StartAtLogonBox.IsChecked == true);
            StartAtLogonStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            try { StartAtLogonBox.IsChecked = _logonStartup.Enabled; }
            catch (Exception readError) when (readError is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { StartAtLogonBox.IsEnabled = false; }
            ShowLogonSettingError(ex);
        }
    }

    private void ShowLogonSettingError(Exception error)
    {
        StartAtLogonStatus.Text = "Windowsの自動起動を設定できません。" + SafeMessage(error);
        StartAtLogonStatus.Visibility = Visibility.Visible;
    }
}
