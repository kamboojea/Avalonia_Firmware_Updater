/**
 * @file Program.cs
 * @author Ali Rahmatinia
 * @brief Avalonia application entry point.
 */

using Avalonia;
using System;

namespace AvaloniaFirmwareUpdater;

class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    /**
     * @brief Builds the Avalonia desktop application.
     */
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
