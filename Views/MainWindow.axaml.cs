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
using System.IO.Compression;
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
    private const string AutoFindComPortOption = "Auto find";
    private const int BroadcastBoardAddress = 0x00;
    private const int DefaultBoardAddress = 0xB1;
    private const int MaxCommLogLinesPerRead = 200;
    private const int MaxLogLines = 700;
    private const double DrivenGearMeshPhaseDegrees = 15;
    private const double DrivenGearToothCount = 12;
    private const double GearDriveStepDegrees = 7;
    private const double LargeGearCenter = 27;
    private const double LargeGearToothCount = 16;
    private const double MediumGearCenter = 21;
    private const double ProgressPulseStep = 18;
    private const double SmallGearCenter = 17;

    private readonly DispatcherTimer boardAddressReloadTimer;
    private readonly DispatcherTimer commLogTailTimer;
    private readonly AppSettings loadedSettings = AppSettingsStore.Load();
    private readonly MainWindowViewModel viewModel = new();
    private readonly StringBuilder bothLogText = new();
    private readonly StringBuilder commLogText = new();
    private readonly StringBuilder localLogText = new();
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
    private long commLogReadOffset;
    private double gearAngle;
    private bool logsVisible = true;
    private bool initializationComplete;
    private double progressPulseOffset = -140;
    private AcpFirmwareUpdate? runningUpdater;
    private string? activeCommLogPath;
    private string? pendingCommLogPort;
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
        DataContext = viewModel;
        viewModel.DescribeBoardAddress = DescribeBoardAddress;
        InitializeProgressAnimationTransforms();
        viewModel.LoadSettings(loadedSettings);

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

        commLogTailTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        commLogTailTimer.Tick += (_, _) => ReadNewCommLogLines();

        SetBoardAddresses(BoardAddressStore.LoadOrCreate(AppendLog), loadedSettings.LastBoardAddress);
        RefreshComPorts();
        ApplyLoadedSettingsToControls();
        StartBoardAddressWatcher();
        Closed += (_, _) =>
        {
            SaveCurrentSettings();
            DisposeWatchers();
        };

        SetStatus(AppStatus.Ready, "Ready");
        SetProgress(0, "Ready");
        SyncViewModelToUi();
        UpdateTimeFields();
        AppendLog("Ready.");
        initializationComplete = true;
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
        viewModel.FirmwarePath = selectedPath;
        SetFirmwareAttention(false);
        SaveCurrentSettings();
        SyncViewModelToUi();
        AppendLog($"Firmware selected: {selectedPath}");
    }

    private void BoardAddressComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedBoardPreview();
        SaveCurrentSettings();
        SyncViewModelToUi();
    }

    private void ComPortComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        viewModel.SelectedComPort = GetSelectedComPort();
        SaveCurrentSettings();
        SyncViewModelToUi();
    }

    private void ComPortComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        RefreshComPorts();
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

        if (boardAddress.Address == BroadcastBoardAddress)
        {
            SetStatus(AppStatus.Warning, "Select board");
            SetProgress(0, "Select board");
            AppendLog("Error: Broadcast #0x00 cannot be used for firmware update. Select the target board, for example EDB #0xB1.");
            return;
        }

        viewModel.FirmwarePath = firmwarePath;
        viewModel.SelectedBoard = boardAddress;
        viewModel.SelectedComPort = GetSelectedComPort();
        SyncViewModelToUi();

        if (!FirmwareFilenameCompatibility.MatchesBoardAddress(firmwarePath, boardAddress, out var fileAddressText))
        {
            var expectedAddress = boardAddress.Address.ToString("X2");
            var error = string.IsNullOrWhiteSpace(fileAddressText)
                ? $"Firmware filename must end with the selected board address, for example -{expectedAddress}.hex."
                : $"Firmware filename address {fileAddressText} does not match selected board {boardAddress.DisplayName} 0x{expectedAddress}.";

            SetStatus(AppStatus.Warning, "Wrong firmware");
            SetProgress(0, "Wrong firmware");
            viewModel.ResultSummary = "Update blocked by firmware filename compatibility check.";
            SyncViewModelToUi();
            AppendLog($"Error: {error}");
            return;
        }

        var selectedComPort = GetSelectedComPort();
        var cancellationTokenSource = new CancellationTokenSource();
        viewModel.SelectedComPort = selectedComPort;
        viewModel.ResultSummary = "Update is running.";
        viewModel.Phase = UpdatePhase.Preflight;
        SaveCurrentSettings();
        SyncViewModelToUi();

        BeginTiming();
        SetBusy(true);
        SetStatus(AppStatus.Running, "Updating");
        SetProgress(0, "Starting update");
        var runStartedAt = DateTime.Now;
        AppendRunBanner(boardAddress, firmwarePath, selectedComPort, runStartedAt);
        AppendCommLogRunBanner(boardAddress, firmwarePath, selectedComPort, runStartedAt);
        AppendLog("Pre-flight summary:");
        AppendLog(viewModel.PreflightSummary);
        if (viewModel.CompatibilitySummary.StartsWith("Compatibility warning:", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog($"Warning: {viewModel.CompatibilitySummary["Compatibility warning: ".Length..]}");
        }
        AppendLog($"Starting update for {boardAddress.DisplayName} {boardAddress.Hex}.");
        AppendLog($"Firmware file: {firmwarePath}");
        AppendLog(selectedComPort is not null
            ? $"Communication port: {selectedComPort}"
            : "Communication port: auto find");

        var succeeded = false;
        updateCancelled = false;
        updateCancellationSource = cancellationTokenSource;
        runningUpdater = new AcpFirmwareUpdate(AppendLog, ReportProgress, DescribeBoardAddress);

        try
        {
            succeeded = await Task.Run(() => RunFirmwareUpdate(firmwarePath, boardAddress.Address, selectedComPort, cancellationTokenSource.Token));
            updateCancelled = updateCancelled || cancellationTokenSource.IsCancellationRequested || runningUpdater.WasCancelled;
            SaveSuccessfulComPort(runningUpdater.LastDetectedCommPort);
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
        }
        finally
        {
            FinishTiming();
            SetBusy(false);
            cancellationTokenSource.Dispose();
            updateCancellationSource = null;
            runningUpdater = null;
        }

        if (succeeded)
        {
            SetStatus(AppStatus.Complete, "Complete");
            SetProgress(100, "Update complete");
            viewModel.Phase = UpdatePhase.Complete;
            viewModel.ResultSummary = $"Complete in {FormatElapsed(updateStopwatch?.Elapsed ?? TimeSpan.Zero)}.";
            SyncViewModelToUi();
            AppendLog($"Programming time: {FormatElapsed(updateStopwatch?.Elapsed ?? TimeSpan.Zero)}");
            return;
        }

        if (updateCancelled)
        {
            SetStatus(AppStatus.Cancelled, "Cancelled");
            SetProgress((int)UpdateProgressBar.Value, "Cancelled");
            viewModel.Phase = UpdatePhase.Cancelled;
            viewModel.ResultSummary = $"Cancelled after {FormatElapsed(updateStopwatch?.Elapsed ?? TimeSpan.Zero)}.";
            SyncViewModelToUi();
            AppendLog($"Update cancelled after {FormatElapsed(updateStopwatch?.Elapsed ?? TimeSpan.Zero)}");
            return;
        }

        SetStatus(AppStatus.Failed, "Failed");
        SetProgress((int)UpdateProgressBar.Value, "Failed");
        viewModel.Phase = UpdatePhase.Failed;
        viewModel.ResultSummary = $"Failed after {FormatElapsed(updateStopwatch?.Elapsed ?? TimeSpan.Zero)}.";
        SyncViewModelToUi();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!updateRunning)
        {
            return;
        }

        updateCancelled = true;
        CancelButton.IsEnabled = false;
        CancelButton.Content = "Cancelling";
        viewModel.Phase = UpdatePhase.Cancelling;
        viewModel.ResultSummary = "Cancel requested; waiting for the active firmware command to finish safely.";
        SetStatus(AppStatus.Cancelling, "Cancelling");
        SetProgress((int)UpdateProgressBar.Value, "Cancelling");
        AppendLog("Cancelling update; communication port will be disconnected safely.");
        SyncViewModelToUi();

        updateCancellationSource?.Cancel();
        runningUpdater?.Cancel();
    }

    private void ClearLogButton_Click(object? sender, RoutedEventArgs e)
    {
        localLogText.Clear();
        commLogText.Clear();
        bothLogText.Clear();
        LocalLogStackPanel.Children.Clear();
        CommLogStackPanel.Children.Clear();
        BothLocalLogStackPanel.Children.Clear();
        BothCommLogStackPanel.Children.Clear();
    }

    private void ExportLogButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var exportPath = ExportLogBundle();
            AppendLog($"Log bundle exported: {exportPath}");
        }
        catch (Exception ex)
        {
            AppendLog($"Error: Failed to export log bundle. {ex.Message}");
        }
    }

    private async void CopyLogButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is null)
        {
            AppendLog("Error: Clipboard is not available.");
            return;
        }

        await topLevel.Clipboard.SetTextAsync(GetSelectedLogText());
        AppendLog("Debug log copied to clipboard.");
    }

    private void LogVisibilityButton_Click(object? sender, RoutedEventArgs e)
    {
        logsVisible = !logsVisible;
        viewModel.LogsVisible = logsVisible;
        ApplyLogVisibility();
        SaveCurrentSettings();
    }

    private void ApplyLogVisibility()
    {
        DebugLogBody.IsVisible = logsVisible;
        ProgressStrip.IsVisible = logsVisible;
        ProgressOnlyBody.IsVisible = !logsVisible;
        CopyLogButton.IsVisible = logsVisible;
        ExportLogButton.IsVisible = logsVisible;
        DebugTitleText.Text = logsVisible ? "Debug output" : "Update progress";
        DebugSubtitleText.Text = logsVisible ? "Live updater diagnostics" : "Debug output is hidden";
        LogVisibilityButton.Content = logsVisible ? "Hide logs" : "Show logs";
    }

    /**
     * @brief Runs the firmware update on a worker thread.
     * @param firmwarePath Valid firmware file path.
     * @param boardAddress Selected ACP board address.
     * @return True when the update completes and verifies.
     */
    private bool RunFirmwareUpdate(string firmwarePath, int boardAddress, string? commPortName, CancellationToken cancellationToken)
    {
        if (runningUpdater is null)
        {
            return false;
        }

        var preferredAutoCommPortName = commPortName is null
            ? AppSettingsStore.Load().LastSuccessfulComPort
            : null;

        return runningUpdater.UpdateFirmware(
            firmwarePath,
            boardAddress,
            commPortName,
            preferredAutoCommPortName,
            cancellationToken);
    }

    private void AppendRunBanner(BoardAddressOption boardAddress, string firmwarePath, string? selectedComPort, DateTime runStartedAt)
    {
        const string bannerLine = "//++++++++++++++++++++++++++++++++++++++++++++++++++";
        var title = $"//                                   Update Board = {boardAddress.DisplayName}";

        AppendLog(bannerLine);
        AppendLog(title);
        AppendLog($"//                                   Date = {runStartedAt:yyyy-MM-dd HH:mm:ss}");
        AppendLog($"//                                   Target = {boardAddress.DisplayName} {boardAddress.Hex}");
        AppendLog($"//                                   Firmware = {Path.GetFileName(firmwarePath)}");
        AppendLog($"//                                   Port = {FormatPortForBanner(selectedComPort)}");
        AppendLog(bannerLine);
    }

    private void AppendCommLogRunBanner(BoardAddressOption boardAddress, string firmwarePath, string? selectedComPort, DateTime runStartedAt)
    {
        const string bannerLine = "//++++++++++++++++++++++++++++++++++++++++++++++++++";
        var title = $"//                                   Update Board = {boardAddress.DisplayName}";

        AppendCommLog(bannerLine);
        AppendCommLog(title);
        AppendCommLog($"//                                   Date = {runStartedAt:yyyy-MM-dd HH:mm:ss}");
        AppendCommLog($"//                                   Target = {boardAddress.DisplayName} {boardAddress.Hex}");
        AppendCommLog($"//                                   Firmware = {Path.GetFileName(firmwarePath)}");
        AppendCommLog($"//                                   Port = {FormatPortForBanner(selectedComPort)}");
        AppendCommLog(bannerLine);
    }

    private static string FormatPortForBanner(string? selectedComPort)
    {
        return string.IsNullOrWhiteSpace(selectedComPort) ? "Auto find" : selectedComPort;
    }

    private void SaveSuccessfulComPort(string? commPortName)
    {
        if (string.IsNullOrWhiteSpace(commPortName))
        {
            return;
        }

        var settings = viewModel.CreateSettings();
        settings.LastSuccessfulComPort = commPortName;

        try
        {
            AppSettingsStore.Save(settings);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save successful COM port: {ex.Message}");
        }
    }

    private string DescribeBoardAddress(int address)
    {
        var match = boardAddresses.FirstOrDefault(boardAddress => boardAddress.Address == address);
        return match is null
            ? $"0x{address:X2}"
            : $"{match.DisplayName} 0x{address:X2}";
    }

    private void ApplyLoadedSettingsToControls()
    {
        if (!string.IsNullOrWhiteSpace(viewModel.FirmwarePath))
        {
            FirmwarePathTextBox.Text = viewModel.FirmwarePath;
        }

        logsVisible = viewModel.LogsVisible;

        if (!string.IsNullOrWhiteSpace(viewModel.SelectedComPort))
        {
            var ports = (ComPortComboBox.ItemsSource as IEnumerable<string>) ?? [];
            ComPortComboBox.SelectedItem = ports.Contains(viewModel.SelectedComPort, StringComparer.OrdinalIgnoreCase)
                ? ports.First(port => string.Equals(port, viewModel.SelectedComPort, StringComparison.OrdinalIgnoreCase))
                : AutoFindComPortOption;
        }

        ApplyLogVisibility();
        SyncViewModelToUi();
    }

    private void SaveCurrentSettings()
    {
        if (!initializationComplete)
        {
            return;
        }

        viewModel.FirmwarePath = FirmwarePathTextBox.Text?.Trim();
        viewModel.SelectedBoard = GetSelectedBoardAddress();
        viewModel.SelectedComPort = GetSelectedComPort();
        viewModel.LogsVisible = logsVisible;

        try
        {
            var settings = viewModel.CreateSettings();
            settings.LastSuccessfulComPort = AppSettingsStore.Load().LastSuccessfulComPort;
            AppSettingsStore.Save(settings);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }

    private void SyncViewModelToUi()
    {
        PreflightSummaryText.Text = viewModel.PreflightSummary;
        CompatibilitySummaryText.Text = viewModel.CompatibilitySummary;
        CompatibilitySummaryText.IsVisible = !viewModel.CompatibilitySummary.StartsWith("Compatibility check", StringComparison.OrdinalIgnoreCase);
        CompatibilitySummaryText.Foreground = viewModel.CompatibilitySummary.StartsWith("Compatibility warning:", StringComparison.OrdinalIgnoreCase)
            ? Brush.Parse("#B86B00")
            : Brush.Parse("#69736D");
        ResultSummaryText.Text = viewModel.ResultSummary;
    }

    private string ExportLogBundle()
    {
        var exportDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AcpFirmwareUpdater",
            "LogExports");
        Directory.CreateDirectory(exportDirectory);

        var exportPath = Path.Combine(exportDirectory, $"FirmwareUpdate_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        using var archive = ZipFile.Open(exportPath, ZipArchiveMode.Create);

        AddZipEntry(archive, "LocalLogs.txt", localLogText.ToString());
        AddZipEntry(archive, "CommLogs.txt", commLogText.ToString());
        AddZipEntry(archive, "BothLogs.txt", bothLogText.ToString());
        AddZipEntry(archive, "Summary.txt", CreateLogBundleSummary());

        if (File.Exists(AppSettingsStore.SettingsPath))
        {
            archive.CreateEntryFromFile(AppSettingsStore.SettingsPath, "settings.json");
        }

        return exportPath;
    }

    private string CreateLogBundleSummary()
    {
        var builder = new StringBuilder();
        builder.AppendLine("ACP Firmware Updater log bundle");
        builder.AppendLine($"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();
        builder.AppendLine("Pre-flight");
        builder.AppendLine(viewModel.PreflightSummary);
        builder.AppendLine(viewModel.CompatibilitySummary);
        builder.AppendLine();
        builder.AppendLine("Result");
        builder.AppendLine(viewModel.ResultSummary);
        builder.AppendLine();
        builder.AppendLine($"Status: {StatusText.Text}");
        builder.AppendLine(StartTimeText.Text);
        builder.AppendLine(ElapsedTimeText.Text);
        builder.AppendLine(FinishTimeText.Text);
        return builder.ToString();
    }

    private static void AddZipEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
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
        commLogTailTimer.Stop();
    }

    private void UpdateSelectedBoardPreview()
    {
        if (BoardAddressComboBox.SelectedItem is not BoardAddressOption selected)
        {
            HexAddressTextBox.Text = string.Empty;
            SelectedTargetText.Text = "No board selected";
            viewModel.SelectedBoard = null;
            return;
        }

        HexAddressTextBox.Text = selected.Hex;
        SelectedTargetText.Text = $"{selected.DisplayName} {selected.Hex}";
        viewModel.SelectedBoard = selected;
    }

    private void SetBusy(bool isBusy)
    {
        updateRunning = isBusy;
        viewModel.IsBusy = isBusy;
        StartButton.IsEnabled = !isBusy;
        CancelButton.IsEnabled = isBusy;
        CancelButton.Content = "Cancel";
        BrowseButton.IsEnabled = !isBusy;
        BoardAddressComboBox.IsEnabled = !isBusy;
        ComPortComboBox.IsEnabled = !isBusy;
        SetProgressAnimation(isBusy);

        if (!isBusy)
        {
            StopCommLogTail();
        }
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
            viewModel.Phase = InferPhase(text);
            SetProgress(percent, text);
            SyncViewModelToUi();
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

    private static UpdatePhase InferPhase(string text)
    {
        if (text.Contains("loading", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("loaded", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("checking firmware", StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePhase.LoadingFirmware;
        }

        if (text.Contains("searching", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("scan", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("trying comport", StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePhase.ScanningPort;
        }

        if (text.Contains("reboot", StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePhase.Rebooting;
        }

        if (text.Contains("eras", StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePhase.Erasing;
        }

        if (text.Contains("program", StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePhase.Programming;
        }

        if (text.Contains("verify", StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePhase.Verifying;
        }

        if (text.Contains("final", StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePhase.Finalising;
        }

        if (text.Contains("complete", StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePhase.Complete;
        }

        return UpdatePhase.Preflight;
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

    private string? GetSelectedComPort()
    {
        var selected = ComPortComboBox.SelectedItem as string;
        return string.IsNullOrWhiteSpace(selected) || selected == AutoFindComPortOption ? null : selected;
    }

    private void RefreshComPorts()
    {
        var selected = ComPortComboBox.SelectedItem as string;
        var ports = new[] { AutoFindComPortOption }
            .Concat(AcpFirmwareUpdate.GetAvailableCommPortNames())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ComPortComboBox.ItemsSource = ports;
        ComPortComboBox.SelectedItem = !string.IsNullOrWhiteSpace(selected) && ports.Contains(selected, StringComparer.OrdinalIgnoreCase)
            ? ports.First(port => string.Equals(port, selected, StringComparison.OrdinalIgnoreCase))
            : AutoFindComPortOption;
    }

    private string GetSelectedLogText()
    {
        return LogTabControl.SelectedIndex switch
        {
            1 => commLogText.ToString(),
            2 => bothLogText.ToString(),
            _ => localLogText.ToString()
        };
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
                var fullLine = $"[{timestamp}] {line}";
                var severity = ClassifyLog(line);

                AddLogLine(LocalLogStackPanel, localLogText, fullLine, severity);
                AddLogLine(BothLocalLogStackPanel, bothLogText, fullLine, severity);
                TryStartCommLogTailFromLocalLog(line);
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

    private void AppendCommLog(string message)
    {
        var normalized = message.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > MaxCommLogLinesPerRead)
        {
            lines = lines[^MaxCommLogLinesPerRead..];
        }

        foreach (var line in lines)
        {
            AddLogLine(CommLogStackPanel, commLogText, line, LogSeverity.Comms);
            AddLogLine(BothCommLogStackPanel, bothLogText, line, LogSeverity.Comms);
        }

        TrimLogLines();
        ScrollLogsToEnd();
    }

    private void AddLogLine(StackPanel targetPanel, StringBuilder targetText, string fullLine, LogSeverity severity)
    {
        targetText.AppendLine(fullLine);

        var line = new SelectableTextBlock
        {
            Text = fullLine,
            Foreground = GetLogBrush(severity),
            FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 0, 0, 2)
        };

        targetPanel.Children.Add(line);
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
            LogSeverity.Comms => Brush.Parse("#9FD7FF"),
            _ => Brush.Parse("#DCE7E1")
        };
    }

    private void TrimLogLines()
    {
        TrimLogPanel(LocalLogStackPanel);
        TrimLogPanel(CommLogStackPanel);
        TrimLogPanel(BothLocalLogStackPanel);
        TrimLogPanel(BothCommLogStackPanel);
    }

    private static void TrimLogPanel(StackPanel panel)
    {
        while (panel.Children.Count > MaxLogLines)
        {
            panel.Children.RemoveAt(0);
        }
    }

    private void ScrollLogsToEnd()
    {
        Dispatcher.UIThread.Post(() =>
        {
            ScrollViewerToEnd(LocalLogScrollViewer);
            ScrollViewerToEnd(CommLogScrollViewer);
            ScrollViewerToEnd(BothLocalLogScrollViewer);
            ScrollViewerToEnd(BothCommLogScrollViewer);
        }, DispatcherPriority.Background);
    }

    private static void ScrollViewerToEnd(ScrollViewer scrollViewer)
    {
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, scrollViewer.Extent.Height);
    }

    private void TryStartCommLogTailFromLocalLog(string line)
    {
        const string detectedOnMarker = "detected on:";

        var markerIndex = line.IndexOf(detectedOnMarker, StringComparison.OrdinalIgnoreCase);
        var markerLength = detectedOnMarker.Length;

        if (markerIndex < 0)
        {
            return;
        }

        var commPort = line[(markerIndex + markerLength)..].Trim();
        if (!string.IsNullOrWhiteSpace(commPort))
        {
            StartCommLogTail(commPort);
        }
    }

    private void StartCommLogTail(string commPort)
    {
        var commLogsDirectory = Path.Combine(AppContext.BaseDirectory, "CommLogs");
        if (!Directory.Exists(commLogsDirectory))
        {
            AppendCommLog($"CommLogs folder not found yet: {commLogsDirectory}");
            pendingCommLogPort = commPort;
            activeCommLogPath = null;
            commLogReadOffset = 0;
            commLogTailTimer.Start();
            return;
        }

        var logPath = Directory
            .EnumerateFiles(commLogsDirectory, $"{commPort}_*.txt")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (logPath is null)
        {
            AppendCommLog($"Waiting for comm log file for {commPort}.");
            pendingCommLogPort = commPort;
            activeCommLogPath = null;
            commLogReadOffset = 0;
            commLogTailTimer.Start();
            return;
        }

        if (string.Equals(activeCommLogPath, logPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        pendingCommLogPort = null;
        activeCommLogPath = logPath;
        commLogReadOffset = new FileInfo(logPath).Length;
        AppendCommLog($"Following {Path.GetFileName(logPath)}");
        commLogTailTimer.Start();
    }

    private void StopCommLogTail()
    {
        ReadNewCommLogLines();
        commLogTailTimer.Stop();
        activeCommLogPath = null;
        pendingCommLogPort = null;
        commLogReadOffset = 0;
    }

    private void ReadNewCommLogLines()
    {
        if (activeCommLogPath is null)
        {
            if (pendingCommLogPort is not null)
            {
                TryAttachPendingCommLog();
            }

            return;
        }

        try
        {
            using var stream = new FileStream(activeCommLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (commLogReadOffset > stream.Length)
            {
                commLogReadOffset = 0;
            }

            stream.Seek(commLogReadOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, leaveOpen: true);
            var text = reader.ReadToEnd();
            commLogReadOffset = stream.Position;

            if (!string.IsNullOrWhiteSpace(text))
            {
                AppendCommLog(text);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void TryAttachPendingCommLog()
    {
        if (pendingCommLogPort is null)
        {
            return;
        }

        var commLogsDirectory = Path.Combine(AppContext.BaseDirectory, "CommLogs");
        if (!Directory.Exists(commLogsDirectory))
        {
            return;
        }

        var logPath = Directory
            .EnumerateFiles(commLogsDirectory, $"{pendingCommLogPort}_*.txt")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (logPath is null)
        {
            return;
        }

        activeCommLogPath = logPath;
        pendingCommLogPort = null;
        commLogReadOffset = new FileInfo(logPath).Length;
        AppendCommLog($"Following {Path.GetFileName(logPath)}");
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
        Success,
        Comms
    }
}
