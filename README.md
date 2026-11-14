# PlaybackMonitorCS

**Version 0.5.0-Beta**  
*Chloe Skywalker*

[![License:
GPL-3.0-only](https://img.shields.io/badge/License-GPL--3.0--only-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-lightgrey.svg)](#windows-快速开始)

[简体中文](#简体中文) \| [English](#english)

------------------------------------------------------------------------

## 简体中文

### 项目简介

**PlaybackMonitorCS** 是一款单路视频播放与人工监看辅助工具。软件基于
**.NET 10** 与 **WPF** 构建，底层直接调用 **FFmpeg**
原生库进行解复用与解码，提供视音频实时检测与复核功能。

### 界面与功能布局

#### 1. 顶部工具栏与状态区

- **核心操作按钮**：打开视频、打开链接、播放列表、媒体信息、播放截图、记录目录、检测设置、提示音设置。
- **快捷标注与告警**：高亮显示的**人工标记**与**错误详情**按钮，以及**告警提示音**全局开关。
- **实时状态与源摘要**：标题栏右侧动态显示当前运行状态（如“暂停，文件检测暂停”）；工具栏下方直观展示当前源文件路径、分辨率、帧率及色深（如
  `1920×1080 · 25.000fps · 8bit`）。

#### 2. 中央原生视频渲染区

- 窗口可直接监看输出的视频画面。

#### 3. 右侧实时监看与音频面板

- **实时监看状态**：顶部状态指示器实时反馈当前流状态（如“未触发告警”或异常警告）。
- **动态多声道电平表**：按源音轨声道布局动态生成独立音柱（例如立体声显示
  `FL / FR`，5.1 显示 6 根，7.1 显示 8 根）。
- **音轨与监听控制**：支持切换**监听音轨**与**监听声道（不改变检测）**（默认支持全部声道下混，或单独监听指定声道）。
- **可折叠辅助面板**：内置**媒体信息 /
  实际播放状态**与**显示与表头行**折叠区，按需展开不遮挡主视野。

#### 4. 底部播放控制与告警记录台

- **播控栏**：集成时间轴进度条（红色标记线直观标示告警发生位置）、播放/暂停、停止、精确时间码定位（`当前时间 / 总时长`）、音量滑块、监听静音及全屏切换（`F11`）。
- **告警记录明细表**：实时记录并展示所有异常事件，包含
  `异常`、`起点`、`确认位置`、`结束 / 时长`、`状态`、`复核`、`实际时间`、`详情`
  等完整字段。
- **复核与导出**：支持勾选**仅本次监看**过滤历史记录，提供**查看截图**、**确认 / 备注**人工复核以及一键**导出 CSV** 报表功能。
- **全局路径栏**：窗口最底部实时显示当前日志文件与截图的本地存储目录（`settings`）。

### 异常检测与流媒体支持

#### 自动异常检测项

- **画面检测**：黑画面、灰画面、疑似静帧、疑似马赛克、噪声花屏。
- **音频与流检测**：整体或指定声道静音、PCM时间间断、损坏包、损坏帧、音视频流中断提示。

#### 支持的输入源与协议

- **本地文件**：支持常见封装与编码格式的本地音视频文件。
- **网络直播流**：支持 **RTMP(S)**、**RTSP(S)**、**HTTP(S) /
  HLS**、**UDP /
  RTP**、**SRT**；可通过网络参数设置自定义初始播放缓冲与接收缓冲。

> **注意：** 直播流仅支持实时检测，不进行本地录制，亦不支持时间轴回看。

### 告警提示音与媒体信息树

- **自定义告警提示音**：在“提示音设置”中可选用不超过 10 秒的 WAV/MP3
  音频，支持试听与独立音量调节（不受节目监听音量与静音状态影响），默认内置短双音。密集触发的同类异常会自动合并提示音，但每条事件仍独立记入底部列表。若自选音频失效将自动回退至内置提示音并报错。
- **独立媒体信息树**：点击顶部“媒体信息”可打开独立窗体，支持逐轨树状结构查看、节点复制及
  JSON
  导出，全程**不中断当前播放**；未播放本地文件时，可直接分析其他本地文件而不影响当前直播源。

### 快捷键与操作指南

| 快捷键          | 功能说明                                   |
|:----------------|:-------------------------------------------|
| `Space`         | 播放 / 暂停切换                            |
| `Ctrl + O`      | 打开本地视频文件                           |
| `Ctrl + S`      | 截取当前监看画面                           |
| `Ctrl + M`      | 添加人工标记                               |
| `Ctrl + G`      | 输入时间码精确定位（或点击底部“定位”按钮） |
| `F11` / `Esc`   | 进入全屏 / 退出全屏                        |
| `左 / 右方向键` | 文件步进 / 定位                            |

- **跳转定位机制**：点击进度条轨道后进度跳转，拖动滑块释放后触发定位；播放器会等待目标帧解码渲染完成后再恢复进度同步，暂停状态下定位仍保持暂停。
- **点击异常回看**：在底部告警记录列表中**双击**对应文件的告警条目，播放器将自动跳转至异常发生前
  2 秒处开始回看（查看历史记录需先加载同一源文件）。
- **直播不支持跳转**

### Windows 快速开始

#### 环境要求

- **操作系统**：Windows 10 / 11 (x64)
- **SDK**：.NET 10 SDK
- **IDE**：Visual Studio 2026 或其他支持 .NET 10 与 WPF 的版本（亦可使用
  .NET CLI 命令行）

#### 构建步骤

1.  在项目输出目录下创建一个名为 `win-x64` 的文件夹。
2.  下载 **FFmpeg Shared Build**，将其中的核心 `.dll` 文件（如
    `avcodec`、`avformat`、`avutil`、`swresample`、`swscale` 等）以及
    `mediainfo.dll` 一并复制到 `win-x64` 目录中。
3.  使用 Visual Studio 打开解决方案并生成，或在终端运行 `dotnet build`。

### 许可证

Copyright © 2026 Chloe Skywalker.

	

- 个人、教育机构、非营利组织及商业企业均可使用、研究和修改本软件。
- 商业企业可以在内部业务环境中部署和使用本软件。
- 仅在组织内部使用、未向组织外分发的修改版本，通常不需要向公众公开。
- 允许免费或收费分发原版及修改版本，但分发时必须遵守 GPLv3，包括适用的版权与许可证声明、修改标识，以及向接收者提供完整对应源代码或 GPLv3 允许的源码获取方式。
- GPLv3 允许商业使用和收费分发；本项目不额外施加禁止商业用途的限制。

### 第三方依赖与许可证

本项目使用或引用以下第三方软件及库。

- **FFmpeg**：FFmpeg 的常规构建通常采用 LGPL 2.1 或更高版本；启用特定
  GPL 组件的构建可能使 FFmpeg 适用 GPL
  条款。实际许可证取决于所使用二进制的版本和编译配置。本项目的构建说明要求用户自行下载
  FFmpeg Shared Build，因此请核对具体发行包的许可证及构建配置。若分发
  FFmpeg
  二进制，须履行该构建对应的许可证义务，包括适用时提供对应源代码、修改及构建信息。详见
  [FFmpeg 官方许可说明](https://ffmpeg.org/legal.html)。
- **FFmpeg.AutoGen 9.0.1.1**：该 NuGet 版本采用 MIT
  License。FFmpeg.AutoGen 是 FFmpeg 的 .NET 绑定，其 MIT
  许可证不改变底层 FFmpeg 原生库的许可证。详见 [NuGet
  包页面](https://www.nuget.org/packages/FFmpeg.AutoGen/9.0.1.1)。
- **MediaInfo / MediaInfoLib**：MediaInfoLib 上游项目采用 BSD 2-Clause
  License。README 当前未指定实际使用的 `mediainfo.dll`
  版本及发行来源，分发该 DLL
  时应以对应发行包附带的许可证和版权声明为准。详见 [MediaInfoLib
  官方仓库](https://github.com/MediaArea/MediaInfoLib)。

第三方组件的许可证及版权声明独立于本项目许可证。更多信息见
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

------------------------------------------------------------------------

## English

### Introduction

**PlaybackMonitorCS** is a single-stream video player and manual
audio/video monitoring tool. Built with **.NET 10** and **WPF**, it
calls native **FFmpeg** libraries directly for demuxing and decoding,
and provides real-time audio/video detection and review features.

### Interface & Feature Layout

#### 1. Top Toolbar & Status Area

- **Main Actions**: Open Video, Open Link, Playlist, Media Info,
  Snapshots, Record Directory, Detection Settings, and Alert Sound
  Settings.
- **Manual Marking & Alerts**: Highlighted **Manual Mark** and **Error
  Details** buttons, together with a global **Alert Sound** toggle.
- **Live Status & Source Summary**: The right side of the title bar
  displays the current operating status (for example, "Paused — file
  detection paused"). The area below the toolbar shows the current
  source path, resolution, frame rate, and bit depth (for example,
  `1920×1080 · 25.000fps · 8bit`).

#### 2. Central Video Rendering Area

- Displays the rendered video output for direct monitoring.

#### 3. Right-Hand Monitoring & Audio Panel

- **Monitoring Status**: The status indicator at the top provides
  real-time feedback on the current stream (for example, "No alert
  triggered" or an anomaly warning).
- **Dynamic Multichannel Audio Meters**: Separate level meters are
  generated according to the source audio track's channel layout (for
  example, `FL / FR` for stereo, 6 meters for 5.1, and 8 meters for
  7.1).
- **Audio Track & Monitoring Controls**: Supports switching the
  **monitored audio track** and the **monitored channel** without
  changing anomaly detection. By default, all channels can be downmixed
  for monitoring, or a specific channel can be monitored separately.
- **Collapsible Auxiliary Panels**: Includes collapsible **Media Info /
  Actual Playback Status** and **Display & Header Rows** panels, which
  can be expanded when needed without obstructing the main viewing area.

#### 4. Bottom Playback Controls & Anomaly Event Log

- **Playback Controls**: Includes a timeline (with a red marker
  indicating the alert position), Play/Pause, Stop, precise timecode
  seeking (`Current Time / Total Duration`), a volume slider, monitoring
  mute, and fullscreen toggle (`F11`).
- **Anomaly Event Log**: Records and displays detected events in real
  time, with fields including `Anomaly`, `Start`, `Confirmed Position`,
  `End / Duration`, `Status`, `Review`, `Event Time`, and `Details`.
- **Review & Export**: The **Current Session Only** filter can be
  enabled to hide historical records. The log also provides **View
  Snapshot**, manual **Confirm / Add Note**, and one-click **Export
  CSV**.
- **Global Path Bar**: The bottom of the window displays the local
  storage paths for log files and screenshots (`settings`).

### Anomaly Detection & Streaming Support

#### Automatic Detection

- **Video Detection**: Black frames, gray frames, suspected frozen
  frames, suspected macroblocking, and noisy/corrupted video artifacts.
- **Audio & Stream Detection**: Overall or per-channel silence, PCM
  timing gaps, corrupted packets, corrupted frames, and audio/video
  stream interruption alerts.

#### Supported Input Sources & Protocols

- **Local Files**: Supports local audio/video files in common container
  and codec formats.
- **Live Streams**: Supports **RTMP(S)**, **RTSP(S)**, **HTTP(S) /
  HLS**, **UDP / RTP**, and **SRT**. Network settings allow the initial
  playback buffer and receive buffer to be configured.

> **Note:** Live streams support real-time monitoring only. Local
> recording and timeline playback/seeking are not supported.

### Alert Sounds & Media Information Tree

- **Custom Alert Sounds**: In **Alert Sound Settings**, you can select a
  WAV or MP3 file no longer than 10 seconds, preview it, and adjust its
  volume independently of the program monitoring volume and mute state.
  A short double beep is included by default. Repeated alerts of the
  same type within a short period are combined into a single sound
  notification, while each event is still recorded separately in the
  event log. If a selected sound file becomes unavailable, the
  application falls back to the built-in sound and reports an error.
- **Separate Media Information Window**: Select **Media Info** on the
  top toolbar to open a separate window. It provides a track-by-track
  tree view, node copying, and JSON export without interrupting current
  playback. When no local file is playing, you can analyze another local
  file without affecting the current live stream.

### Keyboard Shortcuts & Operation Guide

| Shortcut                  | Function                                                                          |
|:--------------------------|:----------------------------------------------------------------------------------|
| `Space`                   | Toggle Play / Pause                                                               |
| `Ctrl + O`                | Open a local video file                                                           |
| `Ctrl + S`                | Capture the current monitoring frame                                              |
| `Ctrl + M`                | Add a manual mark                                                                 |
| `Ctrl + G`                | Enter a timecode for precise seeking (or click the **Seek** button at the bottom) |
| `F11` / `Esc`             | Enter / exit fullscreen                                                           |
| `Left / Right Arrow Keys` | File stepping / seeking                                                           |

- **Seeking Behavior**: Clicking the timeline track moves the playback
  position. Dragging the slider triggers a seek when the slider is
  released. The player waits for the target frame to finish decoding and
  rendering before resuming progress synchronization. If playback is
  paused, seeking keeps it paused.
- **Reviewing Anomalies**: Double-click an anomaly entry for a file in
  the event log to jump to 2 seconds before the anomaly and start
  playback. To review a historical event, first load the same source
  file.
- **Live streams do not support seeking.**

### Windows Quick Start

#### Requirements

- **Operating System**: Windows 10 / 11 (x64)
- **SDK**: .NET 10 SDK
- **IDE**: Visual Studio 2026 or another IDE/version that supports .NET
  10 and WPF. You can also build with the .NET CLI.

#### Build Steps

1.  Create a folder named `win-x64` in the project output directory.
2.  Download an **FFmpeg Shared Build** and copy its required DLL files
    (such as `avcodec`, `avformat`, `avutil`, `swresample`, and
    `swscale`), together with `mediainfo.dll`, into the `win-x64`
    directory.
3.  Open the solution in Visual Studio and build it, or run
    `dotnet build` from a terminal.

### License

Copyright © 2026 Chloe Skywalker.

The original code in **PlaybackMonitorCS** that is owned by Chloe
Skywalker is licensed under the **GNU General Public License, version 3
only (GPL-3.0-only)**. The complete license text is provided in the
repository root as [LICENSE](LICENSE).

- Individuals, educational institutions, non-profit organizations, and
  commercial organizations may use, study, and modify the Software.
- Commercial organizations may deploy and use the Software internally.
- Modifications that remain exclusively within an organization and are
  not distributed outside it generally do not have to be made public.
- The original Software and modified versions may be redistributed free
  of charge or for a fee. Distribution must comply with GPLv3, including
  applicable copyright and license notices, notices of modification, and
  the requirement to provide the complete Corresponding Source or
  another source-code offer permitted by GPLv3.
- GPLv3 permits commercial use and commercial distribution. This project
  does not add a restriction prohibiting commercial use.

### Third-Party Dependencies & Licenses

The following third-party software and libraries are used or referenced
by this project. Their licenses are not changed by the project's
GPL-3.0-only license; each component remains subject to its own
applicable license.

- **FFmpeg**: Standard FFmpeg builds are generally licensed under LGPL
  2.1 or later. Builds that enable certain GPL components may be subject
  to GPL terms. The applicable license depends on the exact binary
  version and build configuration. The build instructions ask users to
  obtain an FFmpeg Shared Build themselves, so users must check the
  license and build configuration of the specific package they use. If
  FFmpeg binaries are redistributed, the distributor must comply with
  the license applicable to that build, including providing
  corresponding source code, modifications, and build information where
  required. See [FFmpeg Legal](https://ffmpeg.org/legal.html).
- **FFmpeg.AutoGen 9.0.1.1**: This NuGet package is licensed under the
  MIT License. FFmpeg.AutoGen provides .NET bindings for FFmpeg; its MIT
  license does not change the license of the underlying native FFmpeg
  libraries. See [FFmpeg.AutoGen 9.0.1.1 on
  NuGet](https://www.nuget.org/packages/FFmpeg.AutoGen/9.0.1.1).
- **MediaInfo / MediaInfoLib**: The upstream MediaInfoLib project uses
  the BSD 2-Clause License. This README does not specify the version or
  supplier of the `mediainfo.dll` binary actually used. When
  redistributing that DLL, follow the license and copyright notices
  supplied with the specific binary package. See the [MediaInfoLib
  repository](https://github.com/MediaArea/MediaInfoLib).

Third-party components retain their own licenses and copyright notices
independently of this project license. See
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for additional details.
