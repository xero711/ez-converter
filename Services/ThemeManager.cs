using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MediaConverter.Services;

public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void ApplySystemTheme(ResourceDictionary resources)
    {
        if (IsDarkMode())
        {
            ApplyDarkTheme(resources);
        }
        else
        {
            ApplyLightTheme(resources);
        }
    }

    private static void ApplyLightTheme(ResourceDictionary resources)
    {
        SetColor(resources, "WindowBackgroundBrush", "#F4F6FA");
        SetColor(resources, "CardBackgroundBrush", "#FFFFFF");
        SetColor(resources, "ControlBackgroundBrush", "#FFFFFF");
        SetColor(resources, "PrimaryTextBrush", "#172033");
        SetColor(resources, "TitleForegroundBrush", "#172033");
        SetColor(resources, "MutedTextBrush", "#526071");
        SetColor(resources, "BorderBrush", "#D6DCE6");
        SetColor(resources, "DropZoneBackgroundBrush", "#F1F5FF");
        SetColor(resources, "DropZoneBorderBrush", "#B9C8E5");
        SetColor(resources, "DropZoneForegroundBrush", "#31568F");
        SetColor(resources, "ButtonHoverBackgroundBrush", "#E8EDF5");
        SetColor(resources, "DisabledControlBackgroundBrush", "#E8ECF2");
        SetColor(resources, "GridHeaderBackgroundBrush", "#E9EEF6");
        SetColor(resources, "GridAlternateRowBrush", "#F6F8FB");
        SetColor(resources, "SelectedItemBackgroundBrush", "#DCE8FF");
        SetColor(resources, "TabBackgroundBrush", "#E9EEF6");
        SetColor(resources, "TabHoverBackgroundBrush", "#DFE6F1");
    }

    private static void ApplyDarkTheme(ResourceDictionary resources)
    {
        SetColor(resources, "WindowBackgroundBrush", "#11151B");
        SetColor(resources, "CardBackgroundBrush", "#1A2029");
        SetColor(resources, "ControlBackgroundBrush", "#242C38");
        SetColor(resources, "PrimaryTextBrush", "#F3F6FA");
        SetColor(resources, "TitleForegroundBrush", "#FFFFFF");
        SetColor(resources, "MutedTextBrush", "#AEB9C8");
        SetColor(resources, "BorderBrush", "#3A4656");
        SetColor(resources, "DropZoneBackgroundBrush", "#182942");
        SetColor(resources, "DropZoneBorderBrush", "#3E75D1");
        SetColor(resources, "DropZoneForegroundBrush", "#C7DBFF");
        SetColor(resources, "ButtonHoverBackgroundBrush", "#303B4A");
        SetColor(resources, "DisabledControlBackgroundBrush", "#252D37");
        SetColor(resources, "GridHeaderBackgroundBrush", "#202833");
        SetColor(resources, "GridAlternateRowBrush", "#1E2631");
        SetColor(resources, "SelectedItemBackgroundBrush", "#264875");
        SetColor(resources, "TabBackgroundBrush", "#1A2029");
        SetColor(resources, "TabHoverBackgroundBrush", "#293342");
    }

    private static void SetColor(ResourceDictionary resources, string key, string color)
    {
        // Brushes declared in XAML may be frozen by WPF when shared by styles.
        // Replace the resource instead of mutating its Color property.
        var parsedColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);
        resources[key] = new SolidColorBrush(parsedColor);
    }
}
