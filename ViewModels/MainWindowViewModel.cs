using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace AvaloniaFirmwareUpdater;

internal sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private string? firmwarePath;
    private BoardAddressOption? selectedBoard;
    private string? selectedComPort;
    private int watchdogSeconds;
    private bool isBusy;
    private bool logsVisible = true;
    private UpdatePhase phase = UpdatePhase.Idle;
    private string preflightSummary = "Select a firmware file and target board.";
    private string resultSummary = "No update has run yet.";
    private string compatibilitySummary = "Compatibility check will run before update.";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? FirmwarePath
    {
        get => firmwarePath;
        set
        {
            if (SetField(ref firmwarePath, value))
            {
                UpdatePreflightSummary();
            }
        }
    }

    public BoardAddressOption? SelectedBoard
    {
        get => selectedBoard;
        set
        {
            if (SetField(ref selectedBoard, value))
            {
                UpdatePreflightSummary();
            }
        }
    }

    public string? SelectedComPort
    {
        get => selectedComPort;
        set
        {
            if (SetField(ref selectedComPort, value))
            {
                UpdatePreflightSummary();
            }
        }
    }

    public int WatchdogSeconds
    {
        get => watchdogSeconds;
        set
        {
            if (SetField(ref watchdogSeconds, Math.Max(0, value)))
            {
                UpdatePreflightSummary();
            }
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        set => SetField(ref isBusy, value);
    }

    public bool LogsVisible
    {
        get => logsVisible;
        set => SetField(ref logsVisible, value);
    }

    public UpdatePhase Phase
    {
        get => phase;
        set => SetField(ref phase, value);
    }

    public string PreflightSummary
    {
        get => preflightSummary;
        private set => SetField(ref preflightSummary, value);
    }

    public string CompatibilitySummary
    {
        get => compatibilitySummary;
        private set => SetField(ref compatibilitySummary, value);
    }

    public string ResultSummary
    {
        get => resultSummary;
        set => SetField(ref resultSummary, value);
    }

    public void LoadSettings(AppSettings settings)
    {
        FirmwarePath = File.Exists(settings.LastFirmwarePath) ? settings.LastFirmwarePath : null;
        SelectedComPort = string.IsNullOrWhiteSpace(settings.LastComPort) ? null : settings.LastComPort;
        WatchdogSeconds = Math.Max(0, settings.WatchdogSeconds);
        LogsVisible = settings.LogsVisible;
    }

    public AppSettings CreateSettings()
    {
        return new AppSettings
        {
            LastFirmwarePath = FirmwarePath,
            LastBoardAddress = SelectedBoard?.Address ?? 0xB1,
            LastComPort = SelectedComPort,
            WatchdogSeconds = WatchdogSeconds,
            LogsVisible = LogsVisible
        };
    }

    public void RefreshCompatibility()
    {
        CompatibilitySummary = GetCompatibilitySummary();
    }

    private void UpdatePreflightSummary()
    {
        var firmwareName = string.IsNullOrWhiteSpace(FirmwarePath)
            ? "No firmware selected"
            : Path.GetFileName(FirmwarePath);
        var sizeText = GetFirmwareSizeText();
        var targetText = SelectedBoard is null ? "No target selected" : $"{SelectedBoard.DisplayName} {SelectedBoard.Hex}";
        var portText = string.IsNullOrWhiteSpace(SelectedComPort) ? "Auto find" : SelectedComPort;
        var watchdogText = WatchdogSeconds > 0 ? $"{WatchdogSeconds}s watchdog" : "Watchdog disabled";

        PreflightSummary = $"Firmware: {firmwareName}{sizeText}\nTarget: {targetText}\nPort: {portText} | {watchdogText}";
        RefreshCompatibility();
    }

    private string GetFirmwareSizeText()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(FirmwarePath) || !File.Exists(FirmwarePath))
            {
                return string.Empty;
            }

            var kiloBytes = new FileInfo(FirmwarePath).Length / 1024.0;
            return $" ({kiloBytes:0.0} KB)";
        }
        catch
        {
            return string.Empty;
        }
    }

    private string GetCompatibilitySummary()
    {
        if (string.IsNullOrWhiteSpace(FirmwarePath) || SelectedBoard is null)
        {
            return "Compatibility check will run before update.";
        }

        var filename = Path.GetFileNameWithoutExtension(FirmwarePath);
        var boardName = SelectedBoard.DisplayName;
        var address = SelectedBoard.Address.ToString("X2");

        if (filename.Contains(boardName, StringComparison.OrdinalIgnoreCase) ||
            filename.Contains(address, StringComparison.OrdinalIgnoreCase))
        {
            return "Compatibility: filename matches the selected target.";
        }

        return $"Compatibility warning: filename does not mention {boardName} or 0x{address}.";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
