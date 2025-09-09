# Madoka Magica Bad Apple

ASCII playback of Bad Apple (Madoka Magica version) directly in the console with synchronized audio using .NET 8.

## Requirements

- .NET 8 SDK & runtime
  - [Download .NET 8.0](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

## Video Selection Logic

The program accepts an optional path (relative or absolute) to a video file.

Behaviour order:

1. If you pass a CLI argument: that path is used (must exist).
2. If no argument: the current working directory (non‑recursive) is scanned for the first file with one of the extensions: `.mp4`, `.mkv`, `.mov`, `.avi`, `.webm`, `.m4v` (alphabetical order).
3. If none is found or the provided path is invalid: the program exits silently.

Only a short in-place "Loading" animation is shown during extraction; after that only ASCII frames are written.

## Usage

Run from the project directory (or any directory containing the target video):

```bash
dotnet run -- <optional-video-path>
```

Examples:

```bash
# 1. Explicit path
dotnet run -- madoka-bad-apple.mp4

# 2. Auto-detect (no arg) – picks first supported video in current folder
dotnet run --

# 3. Absolute path
dotnet run -- /full/path/to/video.mp4
```

### Expected Outcomes

| Input Provided                | Video Present In CWD             | Result                 |
| ----------------------------- | -------------------------------- | ---------------------- |
| Valid path arg                | (ignored)                        | ASCII playback starts  |
| Missing/invalid path arg      | Has at least one supported video | First video is played  |
| No arg                        | No supported video               | Program exits silently |
| Path arg to non-existing file | (any)                            | Program exits silently |

## Build / Publish

### Windows self‑contained publish

```powershell
dotnet publish -r win-x64 -c Release --self-contained true
```

Executable will be under: `bin/Release/net8.0/win-x64/`.

### Linux example run (after publish framework dependent)

```bash
dotnet publish -c Release
./bin/Release/net8.0/MadokaBadApple
```

## Packages Used

| Package                | Version | Purpose                                                         |
| ---------------------- | ------- | --------------------------------------------------------------- |
| Xabe.FFmpeg            | 6.0.2   | Frame & audio extraction via FFmpeg wrappers                    |
| Xabe.FFmpeg.Downloader | 6.0.2   | Downloads FFmpeg binaries at runtime                            |
| Silk.NET.OpenAL        | 2.21.0  | Cross‑platform audio playback (OpenAL)                          |
| SixLabors.ImageSharp   | 3.1.11  | Image loading, resizing, grayscale conversion for ASCII mapping |
| System.Text.Json       | 9.0.8   | (Reserved for potential future serialization needs)             |

Target framework: `.NET 8 (net8.0)`.

## Notes

- The console is cleared/overwritten only by frame content; there is no status/log spam.
- If you need diagnostic output, reintroduce `Console.WriteLine` statements where needed.
- Press Ctrl+C during playback to attempt an early stop (audio stop timing depends on platform).
