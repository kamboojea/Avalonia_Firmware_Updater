namespace AvaloniaFirmwareUpdater;

internal sealed class AppSettings
{
    public string? LastFirmwarePath { get; set; }

    public int LastBoardAddress { get; set; } = 0xB1;

    public string? LastComPort { get; set; }

    public int WatchdogSeconds { get; set; }

    public bool LogsVisible { get; set; } = true;
}
