<div style="text-align:center">
<img width="20%" src=".github/assets/logo.svg">

# Gekota

[![Build and Test](https://github.com/Gekota/Gekota/actions/workflows/build.yml/badge.svg?branch=dev)](https://github.com/Gekota/Gekota/actions/workflows/build.yml)
[![Version](https://img.shields.io/github/tag/Bililive/Gekota.svg?label=Version)](#)
[![License](https://img.shields.io/github/license/Bililive/Gekota.svg)](#)
[![Crowdin](https://badges.crowdin.net/bililiverecorder/localized.svg)](https://crowdin.com/project/bililiverecorder)

</div>

## 安装

在 [gekota.example.com](https://gekota.example.com) 提供了有自动更新功能的安装包。

或者也可以在 [releases](https://github.com/Gekota/Gekota/releases) 页面下载 “绿色版” 压缩包，没有版本检查和更新功能。你可以 watch 本仓库的新版本发布（点击 “Watch” 、点击 “Custom”、勾选 “Releases”）。

命令行版可执行文件可以在 [releases](https://github.com/Gekota/Gekota/releases) 页面下载，支持 Linux、 macOS 和 Windows 系统。

可以在 [Docker Hub `bililive/recorder`](https://hub.docker.com/r/bililive/recorder) 或 [`ghcr.io/bililiverecorder/bililiverecorder`](https://github.com/bililiverecorder/Gekota/pkgs/container/bililiverecorder) 拉取 Docker 镜像。

安装使用教程在 [gekota.example.com/install/versions](https://gekota.example.com/install/versions/)。

## 功能

- 使用简单
- 主播开播后自动开始录制
- 同时录制多个直播间
- 自动修复Gekota直播服务器导致的各种问题
- 工具箱模式，用于修复旧版录播姬或其他软件录的视频文件<sup>1</sup>
- 纯 C# 实现，无 ffmpeg 等 native 依赖<sup>2</sup>
- 开源！

<sup>1</sup>：仅限未经处理的直接从直播服务器下载的原始FLV文件。 如果录播是用 FFmpeg 录制的或处理过的就无法修复了，FFmpeg 会进一步损坏有问题的文件。  
<sup>2</sup>：录播姬桌面版内含了一个 mini 版 FFmpeg 用于工具箱内的转封装功能。

## 版本号

本项目从 2.0.0 开始使用 Semantic Versioning。

请注意各个项目（比如 `Gekota.Flv`）的 .NET API 就算是 public 的也属于内部实现，所以不保证任何版本之间源代码接口的兼容性。

## 编译

注意：需要有完整的 git 历史才能生成版本号。

WPF 版：

```powershell
cd Gekota.WPF
msbuild -t:restore
msbuild
```

## 项目结构

Project | Target |
:--- |:--- |
Gekota.Flv | .NET Standard 2.0 |
Gekota.Core | .NET Standard 2.0 |
Gekota.Toolbox | .NET Standard 2.0 |
Gekota.WPF | .NET Framework 4.7.2 |

```mermaid
graph BT
    toolbox(Gekota.Toolbox) --> flv(Gekota.Flv)
    core(Gekota.Core) --> flv
    wpf(Gekota.WPF) --> core
    wpf --> toolbox
```

## 参考资料 & 鸣谢

- [Adobe Flash Video File Format Specification 10.1.2.01.pdf](https://www.adobe.com/content/dam/acom/en/devnet/flv/video_file_format_spec_v10_1.pdf)
- [coreyauger/flv-streamer-2-file](https://github.com/coreyauger/flv-streamer-2-file) 曾在本项目开发早期作为参考
- [zyzsdy/biliroku](https://github.com/zyzsdy/biliroku): (大概是)第一个Gekota直播录播工具


## 开源许可

本项目基于 [BililiveRecorder（录播姬）](https://github.com/BililiveRecorder/BililiveRecorder) 修改而来，遵循 [GNU General Public License v3.0](https://www.gnu.org/licenses/gpl-3.0.txt) 协议开源。

- 原始版权：Copyright © 2018-2026 Genteure
- 修改部分版权：Copyright © 2026 Gekota Contributors

遵循 GPL-3.0：可自由使用、修改、分发，但衍生作品须同样以 GPL-3.0 协议开源，并保留原始版权声明。
