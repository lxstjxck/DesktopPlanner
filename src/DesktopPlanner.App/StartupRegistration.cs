using System.IO;
using Microsoft.Win32;
namespace DesktopPlanner.App;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopPlanner";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string { Length: > 0 };
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (!enabled) { key.DeleteValue(ValueName, false); return; }
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден путь приложения.");
        var command = $"\"{executable}\"";
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            command += $" \"{typeof(App).Assembly.Location}\"";
        if (command.Length > 260) throw new InvalidOperationException("Путь приложения слишком длинный для автозапуска Windows.");
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
