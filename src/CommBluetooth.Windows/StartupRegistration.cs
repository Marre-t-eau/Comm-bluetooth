using Microsoft.Win32;

namespace CommBluetooth.Windows;

/// <summary>Lancement automatique à l'ouverture de session (clé « Run » de l'utilisateur, sans droits admin).</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CommBluetooth";
    public const string MinimizedArgument = "--minimized";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {MinimizedArgument}");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
