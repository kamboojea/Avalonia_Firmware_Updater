/**
 * @file MainWindow.axaml.cs
 * @author Ali Rahmatinia
 * @brief Main window logic for the ACP firmware updater.
 */

using AcpFirmwareUpdater;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AvaloniaFirmwareUpdater;

/**
 * @brief Handles file selection, board selection, progress display, and firmware update start.
 */
public partial class MainWindow : Window
{
    private const int MaxLogLines = 700;
    private const int MaxWatchdogSeconds = 3600;
    private const double DrivenGearMeshPhaseDegrees = 15;
    private const double DrivenGearToothCount = 12;
    private const double GearDriveStepDegrees = 7;
    private const double LargeGearCenter = 27;
    private const double LargeGearToothCount = 16;
    private const double MediumGearCenter = 21;
    private const double ProgressPulseStep = 18;
    private const double SmallGearCenter = 17;

    private readonly DispatcherTimer boardAddressReloadTimer;
    private readonly StringBuilder debugText = new();
    private readonly RotateTransform hiddenLargeGearTransform = new() { CenterX = LargeGearCenter, CenterY = LargeGearCenter };
    private readonly RotateTransform hiddenMediumGearTransform = new() { CenterX = MediumGearCenter, CenterY = MediumGearCenter };
    private readonly TranslateTransform hiddenProgressPulseTransform = new() { X = -140 };
    private readonly RotateTransform hiddenSmallGearTransform = new() { CenterX = SmallGearCenter, CenterY = SmallGearCenter };
    private readonly RotateTransform progressLargeGearTransform = new() { CenterX = LargeGearCenter, CenterY = LargeGearCenter };
    private readonly RotateTransform progressMediumGearTransform = new() { CenterX = MediumGearCenter, CenterY = MediumGearCenter };
    private readonly DispatcherTimer progressAnimationTimer;
    private readonly TranslateTransform progressPulseTransform = new() { X = -140 };
    private readonly RotateTransform progressSmallGearTransform = new() { CenterX = SmallGearCenter, CenterY = SmallGearCenter };
    private readonly DispatcherTimer updateClockTimer;

    private FileSystemWatcher? boardAddressWatcher;
    private IReadOnlyList<BoardAddressOption> boardAddresses = [];
    private CancellationTokenSource? updateCancellationSource;
    private double gearAngle;
    private bool logsVisible = true;
    private double progressPulseOffset = -140;
    private AcpFirmwareUpdate? runningUpdater;
    private bool updateCancelled;
    private Stopwatch? updateStopwatch;
    private bool updateRunning;
    private DateTime? updateStartTime;
    private DateTime? updateFinishTime;

    /**
     * @brief Sets up the window and loads the editable board-address list.
     */
    public MainWindow()
    {
        InitializeComponent();
        InitializeProgressAnimationTransforms();

        updateClockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        updateClockTimer.Tick += (_, _) => UpdateTimeFields();

        progressAnimationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        progressAnimationTimer.Tick += (_, _) => AnimateProgressVisuals();

        boardAddressReloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        boardAddressReloadTimer.Tick += (_, _) =>
        {
            boardAddressReloadTimer.Stop();
            ReloadBoardAddressesFromJson();
        };

        SetBoardAddresses(BoardAddressStore.LoadOrCreate(AppendLog), null);
        StartBoardAddressWatcher();
        Closed += (_, _) => DisposeWatchers();

        SetStatus(AppStatus.Ready, "Ready");
        SetProgress(0, "Ready");
        UpdateTimeFields();
        AppendLog("Ready.");
    }

    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            AppendLog("Error: Could not open the file picker.");
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select firmware file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Firmware files")
                {
                    Patterns = ["*.bin", "*.hex", "*.fw", "*.img", "*.acp", "*.*"]
                }
            ]
        });

        if (files.Count == 0)
        {
            return;
        }

        var selectedPath = files[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            SetFirmwareAttention(true);
            AppendLog("Error: Selected firmware file is not a local file.");
            return;
        }

        FirmwarePathTextBox.Text = selectedPath;
        SetFirmwareAttention(false);
        AppendLog($"Firmware selected: {selectedPath}");
    }

    private void BoardAddressComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedBoardPreview();
    }

    private async void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        if (updateRunning)
        {
            return;
        }

        var firmwarePath = GetValidatedFirmwarePath();
        if (firmwarePath is null)
        {
            SetFirmwareAttention(true);
            SetStatus(AppStatus.Warning, "Select firmware");
            SetProgress(0, "Select firmware");
            AppendLog("Error: Select a valid firmware file.");
            return;
        }

        SetFirmwareAttention(false);

        var boardAddress = GetSelectedBoardAddress();
        if (boardAddress is null)
        {
            SetStatus(AppStatus.Warning, "Select board");
            SetProgress(0, "Select board");
            AppendLog("Error: Select a board address.");
            return;
        }

        var watchdogSeconds = GetWatchdogSeconds();
        var watchdogMilliseconds = watchdogSeconds * 1000;

        BeginTiming();
        SetBusy(true);
        SetStatus(AppStatus.Running, "Updating");
        SetProgress(0, "Starting update");
        AppendLog($"Starting update for {boardAddress.DisplayName} {boardAddress.Hex}.");
        AppendLog($"Firmware file: {firmwarePath}");
        AppendLog(watchdogSeconds > 0
            ? $"Watchdog interval: {watchdogSeconds}s"
            : "Watchdog interval: disabled");

        var succeeded = false;
        updateCancelled = false;
        updateCancellationSource = new CancellationTokenSource();
        runningUpdater = new AcpFirmwareUpdate(AppendLog, ReportProgress);

        try
        {
            succeeded = await Task.Run(() => RunFirmwareUpdate(firmwarePath, boardAddress.Address, watchdogMilliseconds, updateCancellationSource.Token));
            updateCancelled = updateCancelled || updateCancellationSource.IsCancellationRequested || runningUpdater.WasCancelled;
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
        }
        finally
        {
            FinishTiming();
            SetBusy(false);
            updateCancellationSource.Dispose();
            updateCancellationSource = null;
            runningUpdater = null;
        }

        if (succeeded)
        {
            SetStatus(AppStatus.Complete, "Complete");
            SetProgress(100, "Update complete");
            AppendLog($"Programming time: {FormatElapsed(updateStopwatch?.Elapsed ?? TimeSpan.Zero)}");
            return;
        }

        if (updateCancelled)
        {
            SetStatus(AppStatus.Cancelled, "Cancelled");
            SetProgress((int)UpdateProgressBar.Value, "Cancelled");
            AppendLog($"Update cancelled after {FormatElapsed(updateStopwatch?.Elapsed ?? TimeSpan.Zero)}");
            return;
        }

        SetStatus(AppStatus.Failed, "Failed");
        SetProgress((int)UpdateProgressBar.Value, "Failed");
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!updateRunning)
        {
            return;
        }

        updateCancelled = true;
        CancelButton.IsEnabled = false;
        SetStatus(AppStatus.Cancelling, "Cancelling");
        SetProgress((int)UpdateProgressBar.Value, "Cancelling");
        AppendLog("Cancelling update; communication port will be disconnected safely.");

        updateCancellationSource?.Cancel();
        runningUpdater?.Cancel();
    }

    private void ClearLogButton_Click(object? sender, RoutedEventArgs e)
    {
        debugText.Clear();
        DebugLogStackPanel.Children.Clear();
    }

    private async void CopyLogButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is null)
        {
            AppendLog("Error: Clipboard is not available.");
            return;
        }

        await topLevel.Clipboard.SetTextAsync(debugText.ToString());
        AppendLog("Debug log copied to clipboard.");
    }

    private void LogVisibilityButton_Click(object? sender, RoutedEventArgs e)
    {
        logsVisible = !logsVisible;
        DebugLogBody.IsVisible = logsVisible;
        ProgressStrip.IsVisible = logsVisible;
        ProgressOnlyBody.IsVisible = !logsVisible;
        CopyLogButton.IsVisible = logsVisible;
        DebugTitleText.Text = logsVisible ? "Debug output" : "Update progress";
        DebugSubtitleText.Text = logsVisible ? "Live updater diagnostics" : "Debug output is hidden";
        LogVisibilityButton.Content = logsVisible ? "Hide logs" : "Show logs";
    }

    /**
     * @brief Runs the firmware update on a worker thread.
     * @param firmwarePath Valid firmware file path.
     * @param boardAddress Selected ACP board address.
     * @param watchdogMilliseconds Watchdog interval in milliseconds.
     * @return True when the update completes and verifies.
     */
    private bool RunFirmwareUpdate(string firmwarePath, int boardAddress, int watchdogMilliseconds, CancellationToken cancellationToken)
    {
        if (runningUpdater is null)
        {
            return false;
        }

        return runningUpdater.UpdateFirmware(firmwarePath, boardAddress, watchdogMilliseconds, cancellationToken);
    }

    private void SetBoardAddresses(IReadOnlyList<BoardAddressOption> addresses, int? preferredAddress)
    {
        if (addresses.Count == 0)
        {
            return;
        }

        var selectedAddress = preferredAddress ?? GetSelectedBoardAddress()?.Address;
        boardAddresses = addresses;
        BoardAddressComboBox.ItemsSource = null;
        BoardAddressComboBox.ItemsSource = boardAddresses;

        var selected = selectedAddress.HasValue
            ? boardAddresses.FirstOrDefault(boardAddress => boardAddress.Address == selectedAddress.Value)
            : null;

        BoardAddressComboBox.SelectedItem = selected ?? boardAddresses[0];
        UpdateSelectedBoardPreview();
    }

    private void StartBoardAddressWatcher()
    {
        var configPath = BoardAddressStore.ConfigPath;
        var configDirectory = Path.GetDirectoryName(configPath);
        var configFileName = Path.GetFileName(configPath);

        if (string.IsNullOrWhiteSpace(configDirectory) || string.IsNullOrWhiteSpace(configFileName))
        {
            return;
        }

        boardAddressWatcher = new FileSystemWatcher(configDirectory, configFileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime
        };

        boardAddressWatcher.Changed += (_, _) => QueueBoardAddressReload();
        boardAddressWatcher.Created += (_, _) => QueueBoardAddressReload();
        boardAddressWatcher.Renamed += (_, _) => QueueBoardAddressReload();
        boardAddressWatcher.EnableRaisingEvents = true;
    }

    private void QueueBoardAddressReload()
    {
        Dispatcher.UIThread.Post(() =>
        {
            boardAddressReloadTimer.Stop();
            boardAddressReloadTimer.Start();
        });
    }

    private void ReloadBoardAddressesFromJson()
    {
        var selectedAddress = GetSelectedBoardAddress()?.Address;

        if (!BoardAddressStore.TryLoadExisting(AppendLog, out var reloadedAddresses))
        {
            return;
        }

        SetBoardAddresses(reloadedAddresses, selectedAddress);
        AppendLog("Board-address JSON reloaded.");
    }

    private void DisposeWatchers()
    {
        updateCancellationSource?.Cancel();
        runningUpdater?.Cancel();
        boardAddressWatcher?.Dispose();
        progressAnimationTimer.Stop();
        updateClockTimer.Stop();
        boardAddressReloadTimer.Stop();
    }

    private void UpdateSelectedBoardPreview()
    {
        if (BoardAddressComboBox.SelectedItem is not BoardAddressOption selected)
        {
            HexAddressTextBox.Text = string.Empty;
            SelectedTargetText.Text = "No board selected";
            return;
        }

        HexAddressTextBox.Text = selected.Hex;
        SelectedTargetText.Text = $"{selected.DisplayName} {selected.Hex}";
    }

    private void SetBusy(bool isBusy)
    {
        updateRunning = isBusy;
        StartButton.IsEnabled = !isBusy;
        CancelButton.IsEnabled = isBusy;
        BrowseButton.IsEnabled = !isBusy;
        BoardAddressComboBox.IsEnabled = !isBusy;
        WatchdogNumericUpDown.IsEnabled = !isBusy;
        SetProgressAnimation(isBusy);
    }

    private void SetStatus(AppStatus status, string text)
    {
        var colors = status switch
        {
            AppStatus.Warning => ("#FFF7D6", "#E0B334", "#8B6500", "!", true),
            AppStatus.Cancelling => ("#FFF7D6", "#E0B334", "#8B6500", "!", true),
            AppStatus.Cancelled => ("#FFF7D6", "#E0B334", "#8B6500", "!", true),
            AppStatus.Failed => ("#FFE9E6", "#DE4A39", "#B3261E", "\u2715", true),
            AppStatus.Complete => ("#E8F8EC", "#26A65B", "#12833E", "\u2713", true),
            AppStatus.Running => ("#E9F8F4", "#22CFC0", "#0B6F66", string.Empty, false),
            _ => ("#F7F9FB", "#DCE2DE", "#5D6761", string.Empty, false)
        };

        StatusText.Text = text;
        StatusText.Foreground = Brush.Parse(colors.Item3);
        StatusPill.Background = Brush.Parse(colors.Item1);
        StatusPill.BorderBrush = Brush.Parse(colors.Item2);

        var failed = status == AppStatus.Failed;

        FailureBugIcon.IsVisible = failed;
        ResultIconBadge.IsVisible = colors.Item5 && !failed;
        ResultIconBadge.Background = Brush.Parse(colors.Item1);
        ResultIconText.Text = colors.Item4;
        ResultIconText.Foreground = Brush.Parse(colors.Item3);
    }

    private void SetFirmwareAttention(bool needsAttention)
    {
        FirmwarePathFrame.Classes.Set("inputAttention", needsAttention);
        FirmwarePathTextBox.Classes.Set("inputAttention", needsAttention);
        FirmwareAttentionText.IsVisible = needsAttention;
    }

    private void BeginTiming()
    {
        updateStartTime = DateTime.Now;
        updateFinishTime = null;
        updateStopwatch = Stopwatch.StartNew();
        updateClockTimer.Start();
        UpdateTimeFields();
    }

    private void FinishTiming()
    {
        updateStopwatch?.Stop();
        updateFinishTime = DateTime.Now;
        updateClockTimer.Stop();
        UpdateTimeFields();
    }

    private void UpdateTimeFields()
    {
        StartTimeText.Text = updateStartTime is null
            ? "Start: --"
            : $"Start: {updateStartTime:HH:mm:ss}";

        var elapsed = updateStopwatch?.Elapsed ?? TimeSpan.Zero;
        ElapsedTimeText.Text = $"Elapsed: {FormatElapsed(elapsed)}";

        FinishTimeText.Text = updateFinishTime is null
            ? "Finish: --"
            : $"Finish: {updateFinishTime:HH:mm:ss}";
    }

    private void InitializeProgressAnimationTransforms()
    {
        ProgressLargeGear.RenderTransform = progressLargeGearTransform;
        ProgressMediumGear.RenderTransform = progressMediumGearTransform;
        ProgressSmallGear.RenderTransform = progressSmallGearTransform;
        HiddenLargeGear.RenderTransform = hiddenLargeGearTransform;
        HiddenMediumGear.RenderTransform = hiddenMediumGearTransform;
        HiddenSmallGear.RenderTransform = hiddenSmallGearTransform;
        ProgressPulse.RenderTransform = progressPulseTransform;
        HiddenProgressPulse.RenderTransform = hiddenProgressPulseTransform;
        ProgressSmallGear.IsVisible = false;
        HiddenSmallGear.IsVisible = false;
    }

    private void SetProgressAnimation(bool isActive)
    {
        ProgressGearAssembly.IsVisible = isActive;
        HiddenGearAssembly.IsVisible = isActive;
        ProgressPulse.IsVisible = isActive;
        HiddenProgressPulse.IsVisible = isActive;

        if (isActive)
        {
            progressAnimationTimer.Start();
            return;
        }

        progressAnimationTimer.Stop();
        ResetProgressAnimation();
    }

    private void ResetProgressAnimation()
    {
        gearAngle = 0;
        progressPulseOffset = -140;
        ApplyProgressAnimationTransforms();
    }

    private void AnimateProgressVisuals()
    {
        gearAngle = (gearAngle + GearDriveStepDegrees) % 360;

        var trackWidth = Math.Max(ProgressPulseHost.Bounds.Width, HiddenProgressPulseHost.Bounds.Width);
        if (trackWidth <= 0)
        {
            ApplyProgressAnimationTransforms();
            return;
        }

        var limit = trackWidth + 140;
        progressPulseOffset += ProgressPulseStep;

        if (progressPulseOffset > limit)
        {
            progressPulseOffset = -140;
        }

        ApplyProgressAnimationTransforms();
    }

    private void ApplyProgressAnimationTransforms()
    {
        var driverAngle = NormalizeAngle(gearAngle);
        var drivenAngle = GetMeshedDrivenAngle(driverAngle);

        progressLargeGearTransform.Angle = driverAngle;
        hiddenLargeGearTransform.Angle = driverAngle;
        progressMediumGearTransform.Angle = drivenAngle;
        hiddenMediumGearTransform.Angle = drivenAngle;
        progressSmallGearTransform.Angle = drivenAngle;
        hiddenSmallGearTransform.Angle = drivenAngle;
        progressPulseTransform.X = progressPulseOffset;
        hiddenProgressPulseTransform.X = progressPulseOffset;
    }

    private static double GetMeshedDrivenAngle(double driverAngle)
    {
        var drivenRatio = LargeGearToothCount / DrivenGearToothCount;
        return NormalizeAngle(DrivenGearMeshPhaseDegrees - driverAngle * drivenRatio);
    }

    private static double NormalizeAngle(double angle)
    {
        var normalized = angle % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private void ReportProgress(int percent, string text)
    {
        void Update()
        {
            SetProgress(percent, text);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Update();
            return;
        }

        Dispatcher.UIThread.Post(Update);
    }

    private void SetProgress(int percent, string text)
    {
        var clampedPercent = Math.Clamp(percent, 0, 100);
        var percentageText = $"{clampedPercent}%";

        UpdateProgressBar.Value = clampedPercent;
        HiddenProgressBar.Value = clampedPercent;
        ProgressPercentageText.Text = percentageText;
        HiddenProgressPercentageText.Text = percentageText;
        ProgressStateText.Text = text;
        HiddenProgressStateText.Text = text;
    }

    private string? GetValidatedFirmwarePath()
    {
        var firmwarePath = FirmwarePathTextBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(firmwarePath))
        {
            return null;
        }

        return File.Exists(firmwarePath) ? firmwarePath : null;
    }

    private BoardAddressOption? GetSelectedBoardAddress()
    {
        return BoardAddressComboBox.SelectedItem as BoardAddressOption;
    }

    private int GetWatchdogSeconds()
    {
        var seconds = WatchdogNumericUpDown.Value ?? 0;

        if (seconds <= 0)
        {
            return 0;
        }

        if (seconds > MaxWatchdogSeconds)
        {
            return MaxWatchdogSeconds;
        }

        return (int)seconds;
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
        {
            return $"{(int)elapsed.TotalHours}h {elapsed.Minutes:D2}m {elapsed.Seconds:D2}s";
        }

        return $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
    }

    /**
     * @brief Adds one timestamped line to the debug log.
     * @param message Message to display.
     */
    private void AppendLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");

        void Append()
        {
            var normalized = message.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalized.Split('\n', StringSplitOptions.None);

            foreach (var line in lines)
            {
                AddLogLine(timestamp, line);
            }

            TrimLogLines();
            ScrollLogsToEnd();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Append();
            return;
        }

        Dispatcher.UIThread.Post(Append);
    }

    private void AddLogLine(string timestamp, string message)
    {
        var severity = ClassifyLog(message);
        var fullLine = $"[{timestamp}] {message}";

        debugText.AppendLine(fullLine);

        var line = new SelectableTextBlock
        {
            Text = fullLine,
            Foreground = GetLogBrush(severity),
            FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 0, 0, 2)
        };

        DebugLogStackPanel.Children.Add(line);
    }

    private static LogSeverity ClassifyLog(string message)
    {
        if (message.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase))
        {
            return LogSeverity.Warning;
        }

        if (message.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
            message.Contains(" failed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Update error", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return LogSeverity.Error;
        }

        if (message.Contains("complete", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("successfully", StringComparison.OrdinalIgnoreCase) ||
            message.EndsWith(" successful.", StringComparison.OrdinalIgnoreCase))
        {
            return LogSeverity.Success;
        }

        return LogSeverity.Information;
    }

    private static IBrush GetLogBrush(LogSeverity severity)
    {
        return severity switch
        {
            LogSeverity.Warning => Brush.Parse("#FFD451"),
            LogSeverity.Error => Brush.Parse("#FF6B5A"),
            LogSeverity.Success => Brush.Parse("#65E38D"),
            _ => Brush.Parse("#DCE7E1")
        };
    }

    private void TrimLogLines()
    {
        while (DebugLogStackPanel.Children.Count > MaxLogLines)
        {
            DebugLogStackPanel.Children.RemoveAt(0);
        }
    }

    private void ScrollLogsToEnd()
    {
        Dispatcher.UIThread.Post(() =>
        {
            DebugLogScrollViewer.Offset = new Vector(
                DebugLogScrollViewer.Offset.X,
                DebugLogScrollViewer.Extent.Height);
        }, DispatcherPriority.Background);
    }

    private enum AppStatus
    {
        Ready,
        Running,
        Warning,
        Cancelling,
        Cancelled,
        Failed,
        Complete
    }

    private enum LogSeverity
    {
        Information,
        Warning,
        Error,
        Success
    }
}
