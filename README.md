# Snit

**Snit** is a small desktop video editor for Windows, in the style of Adobe Premiere.
It's built with C#, WPF and FFmpeg.

![Snit main window](docs/screenshot.png)

> **Status:** work in progress. You can import media, edit on the timeline, preview with
> sound, and save projects. Export, titles and effects are next.

## What works

- **Import** video, audio and pictures (Import button, Ctrl+I, or drop files on the media bin)
- **Timeline** with tracks V1, V2, A1, A2: drag media from the bin, move clips (also between tracks),
  trim edges, cut with the razor, delete or ripple-delete, snapping, hide/mute/lock tracks.
  Videos with sound get a linked audio clip that is edited together with the picture.
- **Preview** with sound, frame-accurate scrubbing on the ruler, and frame-by-frame stepping
- **Undo / redo** for every timeline edit
- **Projects** saved as `.snit` files (plain JSON; media files are referenced, never copied)

## Keyboard shortcuts

| Key | Action |
| --- | --- |
| Space | Play / pause |
| ← / → | Previous / next frame |
| Home / End | Go to start / end |
| Delete | Delete selected clips (Shift+Delete: ripple delete) |
| Ctrl+K | Cut clips at the playhead |
| Ctrl+Z / Ctrl+Shift+Z | Undo / redo |
| = / - | Zoom timeline in / out (Ctrl+mouse wheel also zooms) |
| Ctrl+N / Ctrl+O / Ctrl+S | New / open / save project |
| Ctrl+I | Import media |

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
| `src/Snit/Controls` | Custom controls (timeline canvas, time ruler, inspector slider) |
| `src/Snit/Services` | FFmpeg: importing, thumbnails, preview playback, project files |
| `src/Snit/Core` | The project model (media, tracks, clips) and editing rules |
| `tests/Snit.Tests` | Automated tests |

## How the preview works

The preview runs `ffmpeg` in the background and reads raw frames from it, already resized to
960×540 and converted to the project frame rate. Playing or stepping forward keeps the same
`ffmpeg` running, so it is fast; jumping somewhere else starts a new one at that exact frame.
Sound is mixed by `ffmpeg` too and played with [NAudio](https://github.com/naudio/NAudio).

## Tests

```
dotnet test
```

Some tests make tiny test videos with FFmpeg in a temporary folder; they are skipped when FFmpeg
isn't installed.

To refresh the screenshot above: `dotnet run --project src/Snit -- --screenshot docs/screenshot.png`
(you can put a `.snit` file before `--screenshot` to show a project).
