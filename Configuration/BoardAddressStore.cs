using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaloniaFirmwareUpdater;

/**
 * @file BoardAddressStore.cs
 * @author Ali Rahmatinia
 * @brief Reads and writes the editable board-address JSON file.
 */
internal static class BoardAddressStore
{
    private const string FileName = "board-addresses.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly IReadOnlyList<BoardAddressDefinition> DefaultDefinitions =
    [
        new("ACP_ADDRESS_BROADCAST", "0x00"),
        new("ACP_ADDRESS_HWM", "0x11"),
        new("ACP_ADDRESS_CMB", "0x31"),
        new("ACP_ADDRESS_PMB", "0x41"),
        new("ACP_ADDRESS_WMB", "0x42"),
        new("ACP_ADDRESS_WAFER_PMB", "0x43"),
        new("ACP_ADDRESS_DCB", "0x51"),
        new("ACP_ADDRESS_WAFER_DCB", "0x52"),
        new("ACP_ADDRESS_WAFER_DDB", "0x53"),
        new("ACP_ADDRESS_FCB", "0x61"),
        new("ACP_ADDRESS_PEMB", "0x70"),
        new("ACP_ADDRESS_TEMB", "0x71"),
        new("ACP_ADDRESS_BEMB", "0x72"),
        new("ACP_ADDRESS_EEMB", "0x73"),
        new("ACP_ADDRESS_PSB", "0x81"),
        new("ACP_ADDRESS_PFEB", "0x91"),
        new("ACP_ADDRESS_PMU", "0xA1"),
        new("ACP_ADDRESS_EDB", "0xB1"),
    ];

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "firmware.acp.ota-flasher.desktop",
        FileName);

    /**
     * @brief Loads board addresses from JSON, or creates the default file if it is missing.
     * @param log Function used for warning messages.
     * @return Board addresses for the dropdown.
     */
    public static IReadOnlyList<BoardAddressOption> LoadOrCreate(Action<string> log)
    {
        var path = ConfigPath;

        if (!File.Exists(path))
        {
            SeedEditableFile(path, log);
            return BuildDefaults();
        }

        if (TryLoadExisting(log, out var addresses))
        {
            return addresses;
        }
        
        return BuildDefaults();
    }

    /**
     * @brief Loads the existing JSON file without replacing the current UI list on failure.
     * @param log Function used for warning messages.
     * @param addresses Loaded board addresses.
     * @return True if the JSON was valid and contained at least one address.
     */
    public static bool TryLoadExisting(Action<string> log, out IReadOnlyList<BoardAddressOption> addresses)
    {
        addresses = [];

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var definitions = JsonSerializer.Deserialize<List<BoardAddressDefinition>>(json, JsonOptions);
            var loadedAddresses = BuildAddressOptions(definitions);

            if (loadedAddresses.Count == 0)
            {
                log($"Warning: {FileName} did not contain any usable board addresses.");
                return false;
            }

            addresses = loadedAddresses;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log($"Warning: Could not load {FileName}: {ex.Message}");
            return false;
        }
    }

    private static IReadOnlyList<BoardAddressOption> BuildDefaults()
    {
        return BuildAddressOptions(DefaultDefinitions);
    }

    private static List<BoardAddressOption> BuildAddressOptions(IEnumerable<BoardAddressDefinition>? definitions)
    {
        if (definitions is null)
        {
            return [];
        }

        var options = new List<BoardAddressOption>();
        var seenAddresses = new HashSet<int>();

        foreach (var definition in definitions)
        {
            if (!TryCreateOption(definition, out var option))
            {
                continue;
            }

            if (!seenAddresses.Add(option.Address))
            {
                continue;
            }

            options.Add(option);
        }

        return options;
    }

    private static bool TryCreateOption(BoardAddressDefinition definition, out BoardAddressOption option)
    {
        option = default!;

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            return false;
        }

        if (!TryParseAddress(definition.Address, out var address))
        {
            return false;
        }

        option = new BoardAddressOption(definition.Name.Trim(), address);
        return true;
    }

    private static bool TryParseAddress(string? value, out int address)
    {
        address = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("#", StringComparison.Ordinal))
        {
            normalized = normalized[1..];
        }

        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
            return int.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address)
                && address is >= 0 and <= 0xFF;
        }

        return int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out address)
            && address is >= 0 and <= 0xFF;
    }

    private static void SeedEditableFile(string path, Action<string> log)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var bundledPath = Path.Combine(AppContext.BaseDirectory, FileName);
            if (File.Exists(bundledPath))
            {
                File.Copy(bundledPath, path, overwrite: false);
            }
            else
            {
                var json = JsonSerializer.Serialize(DefaultDefinitions, JsonOptions);
                File.WriteAllText(path, json);
            }

            log($"Warning: {FileName} was missing, so a default editable file was created at {path}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Warning: Could not create {FileName}: {ex.Message}. Using built-in defaults.");
        }
    }

    /**
     * @brief JSON row from board-addresses.json.
     */
    private sealed record BoardAddressDefinition(string Name, string Address);
}
