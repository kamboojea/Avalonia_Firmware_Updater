using System;

namespace AvaloniaFirmwareUpdater;

/**
 * @file BoardAddressOption.cs
 * @author Ali Rahmatinia
 * @brief Board-address item used by the dropdown.
 */
internal sealed record BoardAddressOption(string Name, int Address)
{
    private const string Prefix = "ACP_ADDRESS_";

    /**
     * @brief Name shown in the dropdown.
     */
    public string DisplayName => Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
        ? Name[Prefix.Length..]
        : Name;

    /**
     * @brief Hex value shown next to the dropdown.
     */
    public string Hex => $"#0x{Address:X2}";

    /**
     * @brief Returns the dropdown text.
     */
    public override string ToString() => DisplayName;
}
