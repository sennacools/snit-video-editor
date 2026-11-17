# Snit

**Snit** ("cut" in Dutch) is a small desktop video editor for Windows, in the style of Adobe Premiere.
It's built with C#, WPF and FFmpeg.

![Snit main window](docs/screenshot.png)

> **Status:** early work in progress. The main window layout is done. Importing, editing,
> playback and export are being built step by step.

## Requirements

- Windows 10 or 11
- [.NET SDK 10](https://dotnet.microsoft.com/download) (only needed to build Snit)
- **FFmpeg** on your PATH. Snit doesn't include FFmpeg. Install it once with:

  ```
  winget install Gyan.FFmpeg
  ```

  If FFmpeg is missing, Snit shows a message with this command and a **Check again** button.

Optional: install the fonts [Inter](https://rsms.me/inter/) and
[JetBrains Mono](https://www.jetbrains.com/lp/mono/). Without them, Snit uses Segoe UI and Consolas.

## Build and run

```
git clone https://github.com/sennacools/snit-video-editor.git
cd snit-video-editor
dotnet run --project src/Snit
```

Or open `Snit.slnx` in Visual Studio 2022 (17.14 or newer) or Rider and press Run.

## Project layout

| Folder | What's in it |
| --- | --- |
| `src/Snit/Views` | The windows and panels (XAML) |
| `src/Snit/ViewModels` | What the screens show and do (MVVM) |
| `src/Snit/Themes` | Colours, fonts and control styles |
| `src/Snit/Controls` | Custom controls (time ruler, inspector slider) |
| `src/Snit/Services` | Talking to FFmpeg |
| `src/Snit/Core` | Plain logic such as timecodes |

To refresh the screenshot above: `dotnet run --project src/Snit -- --screenshot docs/screenshot.png`
