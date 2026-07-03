using AcpCore;
using AcpCore.Boards;
using Amscreen.Timers.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
        private const int ProgrammingEndPercent = 86;
        private const long ProgrammingPacketBytes = 64;
        private const int ProgrammingStartPercent = 55;
        private const int PmbAddress = 0x41;
        private const int PmuAddress = 0xA1;
        private readonly Action<string> log;
        private readonly object portLock = new();
        private readonly Action<int, string> progress;
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
        internal bool UpdateFirmware(string firmwareFilename, int boardAddress, int watchdogInterval, CancellationToken cancellationToken = default)
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

            if (!GetFirmwareVersionFromFile(firmwareFilename, out fileVersion))
            {
                log($"Error: {firmwareFilename} is invalid firmware file");
                return false;
            }

            ReportProgress(8, "Firmware file loaded");
            if (StopIfCancelled(cancellationToken))
            {
                return false;
            }

            ReportProgress(12, "Searching for board");
            if (!DetectBoard(boardAddress, cancellationToken, out acpCommPort))
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


                if (fileVersion == boardVersion)
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

                if (!InitiateFirmwareUpdate(firmwareFilename))
                {
                    log($"Error: Failed to initiate the update to board at address {boardAddressInHex}");
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

                if (fileVersion != boardVersion)
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
                            ReportProgress(55, "Erasing flash");
                        }
                        break;

                    case AcpResultCode.FirmwareProgramming:
                        writtenBytes = Math.Min(firmwareFileSize, writtenBytes + ProgrammingPacketBytes);

                        var programmingPercent = CalculateProgrammingPercent(writtenBytes, firmwareFileSize);
                        if (resultChanged || programmingPercent != lastProgrammingPercent)
                        {
                            lastProgrammingPercent = programmingPercent;
                            ReportProgress(programmingPercent, $"Programming firmware {FormatBytes(writtenBytes)} / {FormatBytes(firmwareFileSize)}");
                        }
                        break;

                    case AcpResultCode.FirmwareVerifing:
                        if (resultChanged)
                        {
                            ReportProgress(86, "Verifying firmware");
                        }
                        break;

                    case AcpResultCode.FirmwareComplete:
                        log("Firmware update complete");
                        ReportProgress(90, "Firmware transfer complete");
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
                return ProgrammingStartPercent;
            }

            var progressRatio = Math.Clamp((double)writtenBytes / firmwareFileSize, 0, 1);
            var programmingRange = ProgrammingEndPercent - ProgrammingStartPercent;

            return ProgrammingStartPercent + (int)Math.Round(progressRatio * programmingRange);
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

        private bool GetFirmwareVersionFromFile(string firmwareFilename, out string version)
        {
            var firmwareInfo = AcpFirmwareVersion.ExtractFromFile(firmwareFilename);
            version = firmwareInfo.strVersion;
            return firmwareInfo.blnValid;
        }

        private bool DetectBoard(int boardAddress, CancellationToken cancellationToken, out IAcpCommPort acpCommPort)
        {
            acpCommPort = null;

            try
            {
                var commPorts = GetCommPortNames();

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

                    if (acpCommPort.Open(commPort) != AcpResultCode.Success)
                    {
                        DisconnectActiveCommPort(acpCommPort);
                        continue;
                    }

                    if (StopIfCancelled(cancellationToken))
                    {
                        return false;
                    }

                    if (GetBoardId(acpCommPort, boardAddress, out var boardID, 3, 500) == AcpResultCode.Success)
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
            acpCommPort.FlushResponses();
            acpCommPort.FlushNotifications();
            return GetFirmwareVersion(acpCommPort, boardAddress, out firmwareVersion) == AcpResultCode.Success;
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
