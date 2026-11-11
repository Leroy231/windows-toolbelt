using System.IO;

namespace WindowsToolbelt;

internal static class ToolbeltPaths
{
    internal static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsToolbelt");

    internal static readonly string LegacySettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsPomodoro", "settings.json");
}
