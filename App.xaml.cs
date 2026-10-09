using System.Windows;
using MediaConverter.Services;
using Microsoft.Win32;

namespace MediaConverter;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.ApplySystemTheme(Resources);
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        base.OnExit(e);
    }

    private void SystemEvents_UserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.Color)
        {
            Dispatcher.BeginInvoke(() =>
            {
                ThemeManager.ApplySystemTheme(Resources);
            });
        }
    }
}
