# firmware.acp.ota-flasher.desktop

Desktop ACP OTA firmware flasher for updating ACP boards over CAN/serial ACP communication.

Repository:
`https://amscreen.visualstudio.com/Amscreen/_git/firmware.acp.ota-flasher.desktop`

## Features

- Select a firmware image and target board address.
- Validate the firmware filename address before flashing, for example `SIDEA-207360ED-B1.hex` must match board address `0xB1`.
- Select a COM port manually or use Auto find.
- Auto find tries the last successful COM port first.
- View LocalLogs, CommLogs, or both side by side.
- Export a log bundle for diagnostics.
- Uses an `asInvoker` Windows manifest, so the app does not request administrator privileges.

## Configuration

Board addresses are loaded from an editable `board-addresses.json` file in LocalAppData:

`%LOCALAPPDATA%\firmware.acp.ota-flasher.desktop\board-addresses.json`

The bundled default is copied from:

`Configuration\Json\board-addresses.json`

User settings and exported log bundles are also stored under:

`%LOCALAPPDATA%\firmware.acp.ota-flasher.desktop`

## Build

```powershell
dotnet build AvaloniaFirmwareUpdater.sln
```

## Publish Self-Contained

Windows x64:

```powershell
dotnet publish AvaloniaFirmwareUpdater.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish\win-x64
```

Linux x64:

```powershell
dotnet publish AvaloniaFirmwareUpdater.csproj `
  -c Release `
  -r linux-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish\linux-x64
```

Linux ARM64:

```powershell
dotnet publish AvaloniaFirmwareUpdater.csproj `
  -c Release `
  -r linux-arm64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish\linux-arm64
```

Self-contained builds include the .NET runtime. Users do not need to install .NET separately.
