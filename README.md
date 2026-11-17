# BeachLine

BeachLine is a beta music composition application for tablature, percussion, reusable sections and arrangements, and WAV audio tracks. Scores can be saved as portable `.otab` archives with their audio assets. Audio mixdown supports WAV; FLAC, Ogg Vorbis and MP3 export require FFmpeg to be installed separately and available on PATH.

## Build and run

Open `project.godot` with Godot 4.7.2 .NET. Install a compatible .NET SDK and Git LFS, then clone with LFS enabled so `Music/SoundFonts/FluidR3_GM.sf2` contains the actual SoundFont rather than a pointer. Restore NuGet dependencies and build `BeachLine.csproj`.

```sh
git lfs install
git lfs pull
dotnet build BeachLine.csproj
```

The project includes a Windows export preset. A working Godot .NET editor and matching export templates are needed to build a distributable application.

## Tests

Run from this directory:

```sh
dotnet run --project Tests/TabModel/TabModelRegression.csproj
dotnet run --project Tests/Playback/PlaybackRegression.csproj
dotnet run --project Tests/AudioExport/AudioExportRegression.csproj
```

The export regression exercises compressed formats when FFmpeg is available. Godot scene smoke scripts are in `Tests/`; automated checks do not replace interactive visual, audio, and recording-device checks.

## License and credits

BeachLine's original source code is available under the [MIT License](LICENSE): retain its copyright and permission notice when redistributing it. Third-party assets, the SoundFont, and libraries retain their own licenses; see [CREDITS.md](CREDITS.md) and the notices it references. Some locally held artwork is intentionally not included; the application uses fallback imagery in a clean clone.

This software is in beta. Functionality and project-file compatibility may change.
