using AcpCore;
using AcpCore.Boards;
using Amscreen.Timers.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static AcpCore.AcpFramework;

namespace AcpFirmwareUpdater
{
    /**
     * @brief Updates one ACP board with a selected firmware file.
     * @author Ali Rahmatinia
     */
    internal class AcpFirmwareUpdate
    {
        private const double programmingTimeout = 10000 * 60 * 120;
        private const int BoardDetectScanAttempts = 2;
        private const int BoardDetectProbeAttempts = 2;
        private const int BoardDetectProbeTimeoutMs = 350;
        private const int BoardDetectScanRetryDelayMs = 500;
        private const int PortOpenSettleDelayMs = 150;
        private const int FirmwareVersionReadAttempts = 4;
        private const int ResponseDrainDelayMs = 120;
        private const long ProgrammingPacketBytes = 64;
        private const int PmbAddress = 0x41;
        private const int PmuAddress = 0xA1;
        private readonly Action<string> log;
        private readonly object portLock = new();
        private readonly Action<int, string> progress;
        private static readonly FieldInfo? FirmwareFlashAddressField = typeof(AcpFramework).GetField("m_intFlashAddress", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly FieldInfo? FirmwareImageField = typeof(AcpFramework).GetField("m_arrFirmware", BindingFlags.Static | BindingFlags.NonPublic);
        private IAcpCommPort? activeCommPort;
        private bool cancellationLogged;
        private int currentProgressPercent;

        internal bool WasCancelled { get; private set; }

        /**
         * @brief Creates the firmware updater.
         * @param log Function used for debug text.
         * @param progress Function used for progress percentage and status text.
         */
        internal AcpFirmwareUpdate(Action<string>? log = null, Action<int, string>? progress = null)
        {
            this.log = log ?? Console.WriteLine;
            this.progress = progress ?? ((_, _) => { });
        }

        /**
         * @brief Requests the update to stop.
         */
        internal void Cancel()
        {
            WasCancelled = true;
        }

        /**
         * @brief Runs the full firmware update sequence.
         * @param firmwareFilename Firmware file path.
         * @param boardAddress ACP board address selected by the user.
         * @param watchdogInterval Watchdog kick interval in milliseconds.
         * @param cancellationToken Token used by the UI cancel button.
         * @return True if the board reports the new firmware version after reboot.
         */
        internal bool UpdateFirmware(string firmwareFilename, int boardAddress, int watchdogInterval, string? commPortName = null, CancellationToken cancellationToken = default)
        {
            IAcpCommPort acpCommPort;
            string fileVersion;
            string boardVersion;
            string boardAddressInHex = FormatBoardAddress(boardAddress);

            WasCancelled = false;
            cancellationLogged = false;
            ReportProgress(0, "Checking firmware file");

            if (!File.Exists(firmwareFilename))
            {
                log($"Error: Cannot find '{firmwareFilename}'");
                return false;
            }

            var firmwareFileSize = new FileInfo(firmwareFilename).Length;

            log($"Loading firmware file: {FormatBytes(firmwareFileSize)}");
            ReportProgress(4, "Loading firmware file");
            if (!InitiateFirmwareUpdate(firmwareFilename))
            {
                log($"Error: Failed to load firmware file '{firmwareFilename}'");
                return false;
            }

            if (!GetFirmwareVersionFromLoadedImage(out fileVersion))
            {
                log($"Error: {firmwareFilename} is invalid firmware file");
                return false;
            }

            ReportProgress(8, "Firmware file loaded");
            log($"Firmware file loaded: v{fileVersion}, {FormatBytes(firmwareFileSize)}");
            if (StopIfCancelled(cancellationToken))
            {
                return false;
            }

            log("Preparing communication scan.");
            ReportProgress(12, "Searching for board");
            if (!DetectBoard(boardAddress, commPortName, cancellationToken, out acpCommPort))
            {
                if (WasCancelled)
                {
                    return false;
                }

                log($"Error: Failed to detect any board at address {boardAddressInHex}");
                return false;
            }
            try
            {
                if (StopIfCancelled(cancellationToken))
                {
                    return false;
                }

                log($"Board detected at address {boardAddressInHex}");
                ReportProgress(22, "Board detected");

                // Disable CAN-BUS power to avoid the ping-pong issue if the update is aimed @ PMB
                if (boardAddress == PmbAddress)
                {
                    DisableCanBusPowerSupplies(boardAddress, PmbAddress, acpCommPort);
                }

                ReportProgress(28, "Rebooting board");
                if (!RebootBoard(acpCommPort, boardAddress, cancellationToken))
                {
                    if (WasCancelled)
                    {
                        return false;
                    }

                    log($"Error: Failed to reboot board at address {boardAddressInHex}");
                    return false;
                }

                ReportProgress(36, "Reading board version");
                if (StopIfCancelled(cancellationToken))
                {
                    return false;
                }

                if (!GetFirmwareVersionFromBoard(acpCommPort, boardAddress, out boardVersion))
                {
                    if (StopIfCancelled(cancellationToken))
                    {
                        return false;
                    }

                    log($"Error: Failed to retrieve firmware version info from board at address {boardAddressInHex}");
                    return false;
                }
                log($"Board currently using firmware version v{boardVersion}");


                if (VersionsMatch(fileVersion, boardVersion))
                {
                    log($"Warning: The board at address {boardAddressInHex} is already running v{fileVersion}");
                    ReportProgress(100, "Firmware already installed");
                    return false;
                }
                log($"Updating board firmware version from {boardVersion} to {fileVersion}");
                
                
                ReportProgress(45, "Preparing update");

                if (StopIfCancelled(cancellationToken))
                {
                    return false;
                }

                if (!ApplyFirmwareUpdate(acpCommPort, boardAddress, watchdogInterval, firmwareFileSize, cancellationToken))
                {
                    return false;
                }

                ReportProgress(92, "Rebooting updated board");
                if (!RebootBoard(acpCommPort, boardAddress, cancellationToken))
                {
                    if (WasCancelled)
                    {
                        return false;
                    }

                    log($"Error: Failed to reboot board at address {boardAddressInHex}");
                    return false;
                }

                ReportProgress(96, "Verifying board version");
                if (StopIfCancelled(cancellationToken))
                {
                    return false;
                }

                if (!GetFirmwareVersionFromBoard(acpCommPort, boardAddress, out boardVersion))
                {
                    if (StopIfCancelled(cancellationToken))
                    {
                        return false;
                    }

                    log($"Error: Failed to retrieve firmware version info from board at address {boardAddressInHex} after update");
                    return false;
                }

                if (!VersionsMatch(fileVersion, boardVersion))
                {
                    log($"Error: The firmware version on board at address {boardAddressInHex} is v{boardVersion}, not v{fileVersion}");
                    return false;
                }

                log($"Board at {boardAddressInHex} successfully updated to v{fileVersion}");
                ReportProgress(100, "Update complete");
                return true;
            }
            finally
            {
                DisconnectActiveCommPort();
            }
        }

        private bool RebootBoard(IAcpCommPort acpCommPort, int boardAddress, CancellationToken cancellationToken)
        {
            if (StopIfCancelled(cancellationToken))
            {
                return false;
            }

            Reset(acpCommPort, boardAddress);
            if (cancellationToken.WaitHandle.WaitOne(500))
            {
                StopIfCancelled(cancellationToken);
                return false;
            }

            if (StopIfCancelled(cancellationToken))
            {
                return false;
            }

            return GetBoardId(acpCommPort, boardAddress, out _, 10, 500) == AcpResultCode.Success;
        }

        private bool InitiateFirmwareUpdate(string firmwareFilename)
        {
            return AcpFramework.FirmwareUpdateInitialise(firmwareFilename) == AcpResultCode.Success;
        }

        private bool ApplyFirmwareUpdate(IAcpCommPort acpCommPort, int boardAddress, int watchdogInterval, long firmwareFileSize, CancellationToken cancellationToken)
        {
            var programmingTimer = new PollingTimer(programmingTimeout);
            var watchdogKickTimer = new PollingTimer(watchdogInterval);
            AcpResultCode? lastResultCode = null;
            var lastProgrammingPercent = -1;
            long writtenBytes = 0;
            var actualFirmwareSize = GetFirmwareImageSize();
            if (actualFirmwareSize <= 0)
            {
                actualFirmwareSize = firmwareFileSize;
            }

            while (!programmingTimer.Expired())
            {
                if (StopIfCancelled(cancellationToken))
                {
                    return false;
                }

                // Kick the RDM/RPi watchdog to prevent a reboot
                if (watchdogInterval > 0 && watchdogKickTimer.Expired())
                    KickWatchdog(acpCommPort);

                // Service firmware update task
                var acpResultCode = FirmwareUpdateTask(acpCommPort, boardAddress);
                if (StopIfCancelled(cancellationToken))
                {
                    return false;
                }

                var resultChanged = lastResultCode != acpResultCode;
                lastResultCode = acpResultCode;

                // Check the firmware update task result
                switch (acpResultCode)
                {
                    case AcpResultCode.FirmwareErasing:
                        if (resultChanged)
                        {
                            log("Erasing flash");
                            ReportProgress(0, "Erasing flash");
                        }
                        break;

                    case AcpResultCode.FirmwareProgramming:
                        if (TryGetFirmwareProgress(out var flashAddress, out var firmwareImageSize))
                        {
                            actualFirmwareSize = firmwareImageSize;
                            writtenBytes = Math.Min(firmwareImageSize, flashAddress);
                        }
                        else
                        {
                            writtenBytes = Math.Min(actualFirmwareSize, writtenBytes + ProgrammingPacketBytes);
                        }

                        var programmingPercent = CalculateProgrammingPercent(writtenBytes, actualFirmwareSize);
                        if (resultChanged || programmingPercent != lastProgrammingPercent)
                        {
                            lastProgrammingPercent = programmingPercent;
                            var progressText = writtenBytes >= actualFirmwareSize
                                ? "Finalising firmware transfer"
                                : $"Programming firmware {FormatBytes(writtenBytes)} / {FormatBytes(actualFirmwareSize)}";
                            ReportProgress(programmingPercent, progressText);
                        }
                        break;

                    case AcpResultCode.FirmwareVerifing:
                        if (resultChanged)
                        {
                            ReportProgress(100, "Verifying firmware");
                        }
                        break;

                    case AcpResultCode.FirmwareComplete:
                        log("Firmware update complete");
                        ReportProgress(100, "Firmware transfer complete");
                        return true;

                    case AcpResultCode.ErrorPacketSizeExceeded:
                    case AcpResultCode.ErrorAcpSerialPortSendFailed:
                    case AcpResultCode.ErrorReceiveTimeout:
                    case AcpResultCode.ErrorException:
                    case AcpResultCode.ErrorUnknownResultCode:
                    case AcpResultCode.ErrorMemoryNotErased:
                    case AcpResultCode.ErrorAddressInvalid:
                    case AcpResultCode.ErrorFirmwareExceedsBoardSize:
                    case AcpResultCode.ErrorFlashWriteFailed:
                    case AcpResultCode.ErrorChecksumError:
                    case AcpResultCode.ErrorInvalidImage:
                        log($"Update error - {acpResultCode.ToString()}");
                        return false;

                    default:
                        break;
                }
            }
            log($"Firmware timed out after {programmingTimeout/1000}s");
            return false;
        }


        private bool StopIfCancelled(CancellationToken cancellationToken)
        {
            if (!WasCancelled && !cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            WasCancelled = true;
            DisconnectActiveCommPort();

            if (!cancellationLogged)
            {
                cancellationLogged = true;
                log("Update cancelled.");
                ReportProgress(currentProgressPercent, "Cancelled");
            }

            return true;
        }


        private void SetActiveCommPort(IAcpCommPort acpCommPort)
        {
            lock (portLock)
            {
                activeCommPort = acpCommPort;
            }
        }


        private void DisconnectActiveCommPort(IAcpCommPort? expectedCommPort = null)
        {
            IAcpCommPort? commPortToClose;

            lock (portLock)
            {
                if (expectedCommPort is not null && !ReferenceEquals(activeCommPort, expectedCommPort))
                {
                    return;
                }

                commPortToClose = activeCommPort;
                activeCommPort = null;
            }

            if (commPortToClose is null)
            {
                return;
            }

            try
            {
                commPortToClose.Close();
            }
            catch (Exception ex)
            {
                if (!WasCancelled)
                {
                    log($"Warning: Failed to close communication port: {ex.Message}");
                }
            }
        }


        private void ReportProgress(int percent, string text)
        {
            currentProgressPercent = Math.Clamp(percent, 0, 100);
            progress(currentProgressPercent, text);
        }


        private static int CalculateProgrammingPercent(long writtenBytes, long firmwareFileSize)
        {
            if (firmwareFileSize <= 0)
            {
                return 0;
            }

            var progressRatio = Math.Clamp((double)writtenBytes / firmwareFileSize, 0, 1);

            return (int)Math.Round(progressRatio * 100);
        }


        private static long GetFirmwareImageSize()
        {
            return FirmwareImageField?.GetValue(null) is byte[] firmwareImage
                ? firmwareImage.Length
                : 0;
        }


        private static bool TryGetFirmwareProgress(out long flashAddress, out long firmwareImageSize)
        {
            flashAddress = 0;
            firmwareImageSize = GetFirmwareImageSize();

            if (firmwareImageSize <= 0 || FirmwareFlashAddressField?.GetValue(null) is not int currentFlashAddress)
            {
                return false;
            }

            flashAddress = Math.Clamp((long)currentFlashAddress, 0, firmwareImageSize);
            return true;
        }


        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024)
            {
                return $"{bytes / 1024d / 1024d:0.0} MB";
            }

            if (bytes >= 1024)
            {
                return $"{bytes / 1024d:0.0} KB";
            }

            return $"{bytes} B";
        }

        private static bool GetFirmwareVersionFromLoadedImage(out string version)
        {
            version = string.Empty;

            if (FirmwareImageField?.GetValue(null) is not byte[] firmwareImage)
            {
                return false;
            }

            const string versionTag = "####";
            var versionBytes = ExtractTaggedBytes(firmwareImage, versionTag, versionTag);
            if (versionBytes.Length == 0)
            {
                return false;
            }

            version = Regex.Replace(Encoding.UTF8.GetString(versionBytes), @"[^0-9.,]", "");
            return !string.IsNullOrWhiteSpace(version);
        }


        private static byte[] ExtractTaggedBytes(byte[] source, string startTag, string endTag)
        {
            var startPattern = Encoding.ASCII.GetBytes(startTag);
            var endPattern = Encoding.ASCII.GetBytes(endTag);
            var startIndex = IndexOf(source, startPattern, 0);
            if (startIndex < 0)
            {
                return [];
            }

            var valueStart = startIndex + startPattern.Length;
            var endIndex = IndexOf(source, endPattern, valueStart);
            if (endIndex <= valueStart)
            {
                return [];
            }

            var result = new byte[endIndex - valueStart];
            Array.Copy(source, valueStart, result, 0, result.Length);
            return result;
        }


        private static int IndexOf(byte[] source, byte[] pattern, int startIndex)
        {
            if (pattern.Length == 0 || startIndex >= source.Length)
            {
                return -1;
            }

            for (var index = Math.Max(0, startIndex); index <= source.Length - pattern.Length; index++)
            {
                var found = true;
                for (var patternIndex = 0; patternIndex < pattern.Length; patternIndex++)
                {
                    if (source[index + patternIndex] != pattern[patternIndex])
                    {
                        found = false;
                        break;
                    }
                }

                if (found)
                {
                    return index;
                }
            }

            return -1;
        }

        private bool DetectBoard(int boardAddress, string? commPortName, CancellationToken cancellationToken, out IAcpCommPort acpCommPort)
        {
            acpCommPort = null;

            try
            {
                var commPorts = string.IsNullOrWhiteSpace(commPortName)
                    ? GetCommPortNames()
                    : new[] { commPortName.Trim() };

                log($"Scanning {commPorts.Length} communication port(s).");

                for (var scanAttempt = 1; scanAttempt <= BoardDetectScanAttempts; scanAttempt++)
                {
                    if (scanAttempt > 1)
                    {
                        log($"Retrying communication scan ({scanAttempt}/{BoardDetectScanAttempts}).");
                        if (cancellationToken.WaitHandle.WaitOne(BoardDetectScanRetryDelayMs))
                        {
                            StopIfCancelled(cancellationToken);
                            return false;
                        }
                    }

                    // Check each serial port for a PMU (unique to SideA)
                    foreach (string commPort in commPorts)
                    {
                        if (StopIfCancelled(cancellationToken))
                        {
                            return false;
                        }

                        acpCommPort = GetAcpCommPort(commPort);
                        SetActiveCommPort(acpCommPort);

                        log($"Trying comport: {commPort}");

                        var openResult = acpCommPort.Open(commPort);
                        if (openResult != AcpResultCode.Success)
                        {
                            log($"Warning: Could not open {commPort}: {openResult}");
                            DisconnectActiveCommPort(acpCommPort);
                            continue;
                        }

                        if (cancellationToken.WaitHandle.WaitOne(PortOpenSettleDelayMs))
                        {
                            StopIfCancelled(cancellationToken);
                            return false;
                        }

                        if (StopIfCancelled(cancellationToken))
                        {
                            return false;
                        }

                        DrainPendingResponses(acpCommPort);

                        if (GetBoardId(acpCommPort, boardAddress, out var boardID, BoardDetectProbeAttempts, BoardDetectProbeTimeoutMs) == AcpResultCode.Success)
                        {
                            if (!DetectedBoardMatchesSelection(boardID, boardAddress, commPort))
                            {
                                DisconnectActiveCommPort(acpCommPort);
                                return false;
                            }

                            log($"Board at {FormatBoardAddress(boardAddress)}, detected on: {commPort}");
                            acpCommPort.LogCommsEnable(true);
                            return true;
                        }

                        // Not the right response, close the port and move onto the next one.
                        DisconnectActiveCommPort(acpCommPort);
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                if (WasCancelled)
                {
                    return false;
                }

                log($"Error: DetectBoard - {ex.Message}");
                return false;
            }
        }


        private bool GetFirmwareVersionFromBoard(IAcpCommPort acpCommPort, int boardAddress, out string firmwareVersion)
        {
            firmwareVersion = string.Empty;

            for (var attempt = 1; attempt <= FirmwareVersionReadAttempts; attempt++)
            {
                DrainPendingResponses(acpCommPort);

                var result = GetFirmwareVersion(acpCommPort, boardAddress, out var receivedVersion);
                if (result == AcpResultCode.Success && IsFirmwareVersionText(receivedVersion))
                {
                    firmwareVersion = receivedVersion;
                    return true;
                }

                if (result == AcpResultCode.Success)
                {
                    log($"Warning: Ignored non-version reply '{receivedVersion}' while reading firmware version.");
                }

                Thread.Sleep(ResponseDrainDelayMs);
            }

            return false;
        }


        private static void DrainPendingResponses(IAcpCommPort acpCommPort)
        {
            acpCommPort.FlushResponses();
            acpCommPort.FlushNotifications();
            Thread.Sleep(ResponseDrainDelayMs);
            acpCommPort.FlushResponses();
            acpCommPort.FlushNotifications();
        }


        private static bool IsFirmwareVersionText(string firmwareVersion)
        {
            return !string.IsNullOrWhiteSpace(firmwareVersion) &&
                Regex.IsMatch(firmwareVersion.Trim(), @"^\d{1,3}([.,]\d{1,3}){1,3}$");
        }


        private static bool VersionsMatch(string expectedVersion, string actualVersion)
        {
            return string.Equals(NormalizeVersion(expectedVersion), NormalizeVersion(actualVersion), StringComparison.OrdinalIgnoreCase);
        }


        private static string NormalizeVersion(string version)
        {
            return version.Trim().Replace(',', '.');
        }


        /**
         * @brief Checks the detected board address against the selected address.
         */
        private bool DetectedBoardMatchesSelection(object boardId, int expectedAddress, string commPort)
        {
            if (!TryGetDetectedBoardAddress(boardId, out var detectedAddress))
            {
                return true;
            }

            if (detectedAddress == expectedAddress)
            {
                return true;
            }

            log($"Error: Detected board {FormatBoardAddress(detectedAddress)} on {commPort}, but selected target is {FormatBoardAddress(expectedAddress)}.");
            return false;
        }


        /**
         * @brief Reads an address from the ACP board-id object when one is available.
         */
        private static bool TryGetDetectedBoardAddress(object boardId, out int address)
        {
            address = 0;

            if (boardId is null)
            {
                return false;
            }

            var boardIdType = boardId.GetType();
            var addressMemberNames = new[]
            {
                "Address",
                "BoardAddress",
                "AcpAddress",
                "ACPAddress",
                "NodeAddress"
            };

            foreach (var memberName in addressMemberNames)
            {
                var property = boardIdType.GetProperty(memberName);
                if (property is not null && TryConvertAddress(property.GetValue(boardId), out address))
                {
                    return true;
                }

                var field = boardIdType.GetField(memberName);
                if (field is not null && TryConvertAddress(field.GetValue(boardId), out address))
                {
                    return true;
                }
            }

            return false;
        }


        private static bool TryConvertAddress(object? value, out int address)
        {
            address = 0;

            switch (value)
            {
                case byte byteValue:
                    address = byteValue;
                    return true;

                case sbyte signedByteValue when signedByteValue >= 0:
                    address = signedByteValue;
                    return true;

                case short shortValue when shortValue is >= 0 and <= 0xFF:
                    address = shortValue;
                    return true;

                case ushort ushortValue when ushortValue <= 0xFF:
                    address = ushortValue;
                    return true;

                case int intValue when intValue is >= 0 and <= 0xFF:
                    address = intValue;
                    return true;

                case string stringValue:
                    return TryParseAddressText(stringValue, out address);

                default:
                    return false;
            }
        }


        private static bool TryParseAddressText(string value, out int address)
        {
            address = 0;

            var normalized = value.Trim();
            if (normalized.StartsWith("#", StringComparison.Ordinal))
            {
                normalized = normalized[1..];
            }

            if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(normalized[2..], System.Globalization.NumberStyles.HexNumber, null, out address)
                    && address is >= 0 and <= 0xFF;
            }

            return int.TryParse(normalized, out address) && address is >= 0 and <= 0xFF;
        }


        private static string FormatBoardAddress(int boardAddress)
        {
            return $"0x{boardAddress:X2}";
        }



        /**
         * @brief Queries PMU/PMB watchdog values to stop the host from rebooting.
         */
        private void KickWatchdog(IAcpCommPort acpCommPort)
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm)
            {
                // Waferlite
                AcpPMU.GetRpiWatchdogPeriod(acpCommPort, PmuAddress, out _, 0, 10);
            }
            else
            {
                // DS75/D48
                AcpPMB.GetRdmPcWatchdogPeriod(acpCommPort, PmbAddress, out _, 0, 10);

                // Waferlite V2
                AcpWaferPMB.GetRdmPcWatchdogPeriod(acpCommPort, PmuAddress, out _, 0, 10);
            }
        }



        /**
         * @brief Creates the ACP communication object for a serial port or CAN socket.
         */
        private static IAcpCommPort GetAcpCommPort(string strCommPort)
        {
            if (strCommPort.ToLower().StartsWith("can"))
                return new AcpCanBus();

            return new AcpSerialPort();
        }



        /**
         * @brief Gets serial ports and Linux SocketCAN interfaces.
         */
        private static string[] GetCommPortNames()
        {
            string[] arrCommPorts = new string[] { };

            try
            {
                // Get array of serial comm ports (COMPORTx, ttyUSBx and ttyACMx)
                var commPorts = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);

                // On Linux, include SocketCAN interfaces such as can0/can1.
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                    RuntimeInformation.ProcessArchitecture == Architecture.Arm)
                {
                    var networkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();

                    foreach (System.Net.NetworkInformation.NetworkInterface networkInterface in networkInterfaces)
                    {
                        if (networkInterface.Id.StartsWith("can", StringComparison.OrdinalIgnoreCase))
                        {
                            commPorts.Add(networkInterface.Id);
                        }
                    }
                }

                arrCommPorts = commPorts.ToArray();
                return arrCommPorts.OrderByDescending(d => d).ToArray();
            }
            catch
            {
                return arrCommPorts;
            }
        }


        internal static string[] GetAvailableCommPortNames()
        {
            return GetCommPortNames();
        }



        /**
         * @brief Disables power supplies for a given board address.
         * 
         * @param boardAddress The address of the board.
         * @param pmbAddress The address of the PMB.
         * @param acpCommPort The ACP communication port.
         */
        private void DisableCanBusPowerSupplies(int boardAddress, int pmbAddress, IAcpCommPort acpCommPort)
        {
            if (boardAddress != pmbAddress)
            {
                return;
            }
            // Disabling CAN-BUS1 Power
            DisableCanBusPowerSupply(acpCommPort, boardAddress, AcpPMB.PowerSupplies.CanBus1);

            // Disabling CAN-BUS2 Power
            DisableCanBusPowerSupply(acpCommPort, boardAddress, AcpPMB.PowerSupplies.CanBus2);
        }

        /**
         * @brief Disables a specific power supply for a given board address.
         * 
         * @param acpCommPort The ACP communication port.
         * @param boardAddress The address of the board.
         * @param supply The power supply to disable.
         */
        private void DisableCanBusPowerSupply(IAcpCommPort acpCommPort, int boardAddress, AcpPMB.PowerSupplies supply)
        {
            try
            {
                var result = AcpPMB.SetPowerSupplyOutputState(acpCommPort, boardAddress, supply, false);
                var successMsg = $"Disabling PMB({FormatBoardAddress(boardAddress)}) CAN-BUS power {supply} successful.";
                var failureMsg = $"Disabling PMB({FormatBoardAddress(boardAddress)}) CAN-BUS power {supply} failed.";

                log(result == AcpResultCode.Success ? successMsg : failureMsg);
            }
            catch (Exception ex)
            {
                log($"Failed to disable PMB({FormatBoardAddress(boardAddress)}) CAN-BUS power {supply}: {ex.Message}");
            }
        }

    }
}
