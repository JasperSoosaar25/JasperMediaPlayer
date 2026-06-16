# Jasper Media Player — Windows 11 Fluent VLC-engine starter

This is a tiny WinUI 3 media-player starter that uses the real VLC playback engine through VideoLAN's official LibVLCSharp/LibVLC NuGet packages.

It is meant for Windows 11 on x64 PCs like Jasper's HP Victus 15.

## What this is

- Real playback backend: LibVLC / VLC engine via NuGet
- Real Windows 11 UI stack: WinUI 3 + Windows App SDK
- Windows 11 visual styling: Mica backdrop, Fluent controls, native rounded-window behavior
- No separate VLC install needed

## What this is not

- Not the official VLC desktop app rebuilt from source
- Not allowed to ship as "VLC" with the VLC cone/logo unless you follow VideoLAN branding/trademark rules
- Not a fake skin over old WinForms/WPF controls

## Install the minimum build tools

1. Install Visual Studio 2022 Community.
2. In the Visual Studio Installer, select only this workload:
   - WinUI application development
3. Open Windows Settings > System > Advanced > Developer Mode, and turn Developer Mode on.

## Build from Visual Studio

1. Unzip this folder somewhere simple, for example:
   C:\Users\jaspe\source\repos\JasperMediaPlayer
2. Open Visual Studio 2022.
3. Choose "Open a project or solution".
4. Open:
   src\JasperMediaPlayer.csproj
5. At the top toolbar, choose:
   - Configuration: Release
   - Platform: x64
6. Press Ctrl+Shift+B.
7. Press F5 to run it.

## Build from PowerShell

Open PowerShell in the unzipped folder, then run:

```powershell
dotnet restore .\src\JasperMediaPlayer.csproj
dotnet build .\src\JasperMediaPlayer.csproj -c Release -r win-x64
```

Then run:

```powershell
.\src\bin\Release\net8.0-windows10.0.19041.0\win-x64\JasperMediaPlayer.exe
```

## Make a shareable folder build

```powershell
dotnet publish .\src\JasperMediaPlayer.csproj -c Release -r win-x64 --self-contained false -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true
```

Your app folder will be here:

```text
src\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\
```

Zip the whole publish folder, not just the EXE, because the VLC native plugins and WinUI files are next to it.

## Controls

- Open File: choose a local video/audio file
- URL box: paste a stream URL and press Play URL
- Play/Pause
- Stop
- Seek slider
- Volume slider

## Notes

The first build can take a bit because NuGet downloads the Windows App SDK, LibVLCSharp, and VideoLAN.LibVLC.Windows packages.
