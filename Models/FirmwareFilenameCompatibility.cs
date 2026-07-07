using System;
using System.Globalization;
using System.IO;

namespace AvaloniaFirmwareUpdater;

internal static class FirmwareFilenameCompatibility
{
    public static bool MatchesBoardAddress(string firmwarePath, BoardAddressOption boardAddress, out string fileAddressText)
    {
        if (!TryGetFileAddress(firmwarePath, out var fileAddress, out fileAddressText))
        {
            return false;
        }

        return fileAddress == boardAddress.Address;
    }

    public static bool TryGetFileAddress(string firmwarePath, out int address, out string addressText)
    {
        address = 0;
        addressText = string.Empty;

        var filename = Path.GetFileNameWithoutExtension(firmwarePath);
        if (string.IsNullOrWhiteSpace(filename))
        {
            return false;
        }

        var separatorIndex = filename.LastIndexOf('-');
        if (separatorIndex < 0 || separatorIndex == filename.Length - 1)
        {
            return false;
        }

        addressText = filename[(separatorIndex + 1)..].Trim();
        var normalized = addressText.TrimStart('#');
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        if (!int.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address) ||
            address is < 0 or > 0xFF)
        {
            return false;
        }

        addressText = $"0x{address:X2}";
        return true;
    }
}
