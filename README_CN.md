# Gekota

一个简单实用的 B 站直播录播工具。

Gekota 把录播流程里比较繁琐的部分自动化了：开播后自动开始录制，录制过程中可以边看边手动切片、截屏，录完可以一键转换弹幕、压制视频，最后自动投稿到 B 站。

大部分时候，你只需要把直播间加进来，剩下的交给它。

## 功能

- **自动录制** —— 添加直播间后，主播开播时自动开始录制
- **多直播间同时录制** —— 可以同时录制多个直播间
- **实时播放** —— 录制过程中可以边录边看
- **手动切片 / 截屏** —— 观看直播时，遇到想保留的片段或画面，点击播放页右下角的按钮，即可截取当前正在录制的内容。这是手动操作，不是自动处理
- **弹幕转换** —— 将录播弹幕转换为 B 站可用的格式，投稿时自带弹幕
- **视频压制** —— 内置 FFmpeg，可以将录像压缩到更小的体积
- **自动上传** —— 压制完成后自动投稿，标题、封面、分区、标签、简介都可以提前设置

## 安装

1. 在 [Releases](https://github.com/kaeru214-gua/Gekota/releases) 页面下载最新的 `Gekota-Setup-x.y.z.exe`
2. 双击运行，按提示完成安装
3. 安装完成后，桌面和开始菜单会生成快捷方式

**环境要求：**

- Windows 10 1803 及以上 / Windows 11（系统自带 .NET Framework 4.7.2）
- 实时播放功能需要 WebView2 Runtime（大多数 Windows 10/11 已自带，如缺失可自行安装）

## 使用

1. **登录** —— 在「用户」页面扫码登录 B 站账号
2. **添加房间** —— 在「房间列表」添加想要录制的直播间
3. **开始录制** —— 主播开播后会自动开始录制
4. **实时播放** —— 打开「实时播放」页面，可以边录边看
5. **切片 / 截屏** —— 观看时点击播放页右下角的按钮，切片会把当前正在录制的这一段单独保存下来，截屏会保存当前画面
6. **弹幕转换 / 压制** —— 在「工具箱」中转换弹幕、压制视频
7. **自动上传** —— 压制完成后自动投稿到 B 站

**几点说明：**

- 切片和截屏是手动操作，点击按钮时截取的是当前正在录制的内容
- 可以在工作目录放一个 `tag.txt`（一行一个标签），上传时会自动带上
- 简介同样可以放在 `简介.txt` 里
- 自动上传需要先在「用户」页面登录
- 第一次使用可以先录一小段，确认整个流程是否正常

## 从源码构建

需要 Visual Studio 2022 或更高版本，并安装「.NET 桌面开发」工作负载。

```powershell
msbuild Gekota.sln /p:Configuration=Release
```

也可以直接用 Visual Studio 打开 `Gekota.sln`，切换到 Release 后编译。

## 开源许可

本项目基于 [BililiveRecorder（录播姬）](https://github.com/BililiveRecorder/BililiveRecorder) 修改而来，遵循 [GNU General Public License v3.0](https://www.gnu.org/licenses/gpl-3.0.txt) 开源。

- 原始版权：Copyright © 2018-2026 Genteure
- 修改部分版权：Copyright © 2026 Gekota Contributors

遵循 GPL-3.0：可自由使用、修改、分发，但衍生作品须同样以 GPL-3.0 协议开源，并保留原始版权声明。

## 参考与致谢

本项目的开发离不开下面这些开源项目和工具，感谢它们的作者：

### 核心参考

- [BililiveRecorder（录播姬）](https://github.com/BililiveRecorder/BililiveRecorder) —— 本项目的起点，录制核心与 FLV 处理参考于此
- [bilibili-API-collect](https://github.com/SocialSisterYi/bilibili-API-collect) —— 直播 / 投稿接口文档参考
- [biliup-rs](https://github.com/biliup/biliup-rs) —— 投稿接口的参考实现

### 视频处理

- [DanmakuFactory](https://github.com/hihkm/DanmakuFactory) —— 弹幕转换
- [ShanaEncoder](https://shana.pe.kr/) —— 提供 FFmpeg 二进制，用于压制 / 转封装
- [FFmpeg](https://ffmpeg.org/) —— 音视频处理

### 界面与运行时

- [ModernWpf](https://github.com/Kinnara/ModernWpf) —— UI 控件库
- [LibVLCSharp](https://github.com/videolan/libvlcsharp) / [VLC](https://www.videolan.org/) —— 实时播放
- [WebView2](https://learn.microsoft.com/microsoft-edge/webview2/) —— 网页渲染
- [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) —— 托盘图标
- [WPF Localize Extension](https://github.com/XAMLMarkupExtensions/WPFLocalizeExtension) —— 多语言

### 其他库

- [Newtonsoft.Json](https://www.newtonsoft.com/json) —— JSON 处理
- [Serilog](https://serilog.net/) —— 日志
- [QRCoder](https://github.com/codebude/QRCoder) —— 二维码生成
- [CliWrap](https://github.com/Tyrrrz/CliWrap) —— 命令行进程封装
- [Microsoft.Toolkit.Uwp.Notifications](https://github.com/CommunityToolkit/WindowsCommunityToolkit) —— 系统通知

### 资料

- [Adobe Flash Video File Format Specification 10.1.2.01.pdf](https://www.adobe.com/content/dam/acom/en/devnet/flv/video_file_format_spec_v10_1.pdf) —— FLV 格式说明
- [coreyauger/flv-streamer-2-file](https://github.com/coreyauger/flv-streamer-2-file) —— 开发早期参考
- [zyzsdy/biliroku](https://github.com/zyzsdy/biliroku) —— 早期的 B 站直播录制工具