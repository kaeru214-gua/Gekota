# Gekota

A simple and practical Bilibili live recording tool.

Gekota automates the repetitive parts of the recording workflow: it starts recording when a stream goes live, lets you clip and screenshot manually while watching, converts danmaku and compresses the video with one click, and finally uploads to Bilibili automatically.

Most of the time, all you need to do is add the room.

## Features

- **Auto-record** — recording starts automatically when the streamer goes live
- **Multi-room recording** — record multiple rooms at the same time
- **Real-time playback** — watch while recording
- **Manual clip / screenshot** — while watching, click the button at the bottom-right of the playback page to capture the segment or frame currently being recorded. This is manual, not automatic
- **Danmaku conversion** — convert recorded danmaku into a Bilibili-compatible format, so uploads include danmaku
- **Video compression** — bundled FFmpeg, compress recordings to a smaller size
- **Auto-upload** — uploads automatically after compression; title, cover, category, tags and description can be pre-configured

## Installation

1. Download the latest `Gekota-Setup-x.y.z.exe` from the [Releases](https://github.com/kaeru214-gua/Gekota/releases) page
2. Run it and follow the steps
3. Shortcuts are created on the desktop and start menu

**Requirements:**

- Windows 10 1803 or later / Windows 11 (.NET Framework 4.7.2 included)
- WebView2 Runtime for real-time playback (included with most Windows 10/11; install it if missing)

## Usage

1. **Login** — scan the QR code on the "User" page
2. **Add room** — add the live rooms you want to record
3. **Record** — starts automatically when the streamer goes live
4. **Watch** — open the "Live Playback" page to watch while recording
5. **Clip / Screenshot** — click the button at the bottom-right. Clip saves the currently recorded segment; screenshot saves the current frame
6. **Danmaku / Compression** — process them in the "Toolbox"
7. **Upload** — uploads automatically after compression

**Notes:**

- Clip and screenshot are manual; they capture the segment currently being recorded
- Put a `tag.txt` in the working directory (one tag per line) and tags will be filled in automatically
- The description can be placed in `简介.txt`
- Auto-upload requires logging in on the "User" page
- For a first run, record a short segment to verify the whole flow works

## Build from source

Requires Visual Studio 2022 or later with the ".NET desktop development" workload.

```powershell
msbuild Gekota.sln /p:Configuration=Release
```

Or open `Gekota.sln` in Visual Studio, switch to Release and build.

## License

This project is a modified fork of [BililiveRecorder](https://github.com/BililiveRecorder/BililiveRecorder) and is licensed under the [GNU General Public License v3.0](https://www.gnu.org/licenses/gpl-3.0.txt).

- Original copyright: Copyright © 2018-2026 Genteure
- Modifications: Copyright © 2026 Gekota Contributors

Under GPL-3.0 you are free to use, modify and redistribute, but derivative works must also be released under GPL-3.0 and must retain the original copyright notice.

## References & Acknowledgements

This project would not exist without the following open-source projects and tools. Thanks to all their authors:

### Core references

- [BililiveRecorder](https://github.com/BililiveRecorder/BililiveRecorder) — the starting point of this project; recording core and FLV handling are based on it
- [bilibili-API-collect](https://github.com/SocialSisterYi/bilibili-API-collect) — documentation reference for live/upload APIs
- [biliup-rs](https://github.com/biliup/biliup-rs) — reference implementation for the upload API

### Video processing

- [DanmakuFactory](https://github.com/hihkm/DanmakuFactory) — danmaku conversion
- [ShanaEncoder](https://shana.pe.kr/) — provides the FFmpeg binary used for compression/remux
- [FFmpeg](https://ffmpeg.org/) — audio/video processing

### UI & runtime

- [ModernWpf](https://github.com/Kinnara/ModernWpf) — UI control library
- [LibVLCSharp](https://github.com/videolan/libvlcsharp) / [VLC](https://www.videolan.org/) — real-time playback
- [WebView2](https://learn.microsoft.com/microsoft-edge/webview2/) — web rendering
- [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) — tray icon
- [WPF Localize Extension](https://github.com/XAMLMarkupExtensions/WPFLocalizeExtension) — localization

### Other libraries

- [Newtonsoft.Json](https://www.newtonsoft.com/json) — JSON handling
- [Serilog](https://serilog.net/) — logging
- [QRCoder](https://github.com/codebude/QRCoder) — QR code generation
- [CliWrap](https://github.com/Tyrrrz/CliWrap) — command-line process wrapper
- [Microsoft.Toolkit.Uwp.Notifications](https://github.com/CommunityToolkit/WindowsCommunityToolkit) — system notifications

### Documentation

- [Adobe Flash Video File Format Specification 10.1.2.01.pdf](https://www.adobe.com/content/dam/acom/en/devnet/flv/video_file_format_spec_v10_1.pdf) — FLV format
- [coreyauger/flv-streamer-2-file](https://github.com/coreyauger/flv-streamer-2-file) — early development reference
- [zyzsdy/biliroku](https://github.com/zyzsdy/biliroku) — an early Bilibili live recording tool