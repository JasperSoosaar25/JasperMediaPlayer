# Contributing

Thanks for helping improve Jasper Media Player.

## Good first contributions

- Fix UI spacing or small visual bugs
- Improve build instructions
- Add playback test URLs
- Improve error messages
- Add small media-player features like drag-and-drop or recent files

## Before making a pull request

1. Open an issue or check if one already exists.
2. Keep the change focused.
3. Build the app locally on Windows.
4. Test local file playback and at least one stream URL.

## Build command

```powershell
dotnet restore .\src\JasperMediaPlayer.csproj
dotnet build .\src\JasperMediaPlayer.csproj -c Release -r win-x64
```

## Pull request checklist

- [ ] The app builds successfully
- [ ] Local file playback still works
- [ ] Stream URL playback still works
- [ ] The UI still looks correct on Windows 11
- [ ] The README/build docs are updated if needed

## Branding note

Please keep the app name as Jasper Media Player. Do not use the VLC name, cone, or official branding for this project.
