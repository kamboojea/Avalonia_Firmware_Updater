namespace AvaloniaFirmwareUpdater;

internal enum UpdatePhase
{
    Idle,
    Preflight,
    LoadingFirmware,
    ScanningPort,
    Rebooting,
    Erasing,
    Programming,
    Verifying,
    Finalising,
    Complete,
    Cancelling,
    Cancelled,
    Failed
}
