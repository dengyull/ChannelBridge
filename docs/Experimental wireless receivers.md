# Experimental wireless receivers / 实验性无线接收

This is a local experimental build, not a verified wireless-audio release. Requires Windows 10 2004 (19041) or later. Both receivers default to off and are independent of the saved multichannel routing preset. Closing the UI leaves explicitly enabled receivers running. An iPhone has played music and another video source through the AirPlay service; the Bilibili app remains silent. A2DP hardware playback, broader service-session compatibility and long-running stability have **not** been verified.

这是本地实验版，需要 Windows 10 2004（19041）及以上。A2DP 和 AirPlay 默认关闭，开关独立于音频路由预设；已启用的接收功能不随 UI 关闭。已在 iPhone 上确认音乐和其他视频来源可经 AirPlay 服务发声，哔哩哔哩 App 仍无声。**A2DP 实机播放、广泛的服务会话兼容性和长时间稳定性尚未验证。**

## A2DP Sink

Open **Advanced settings → Experimental wireless receivers**. Pair the phone in Windows Bluetooth settings first. Enable A2DP and click **Apply switch**, select a discovered device, then **Connect and auto-reconnect**. The service remembers up to eight selected devices (this does not guarantee simultaneous audio streams). Disconnect removes that device's reconnect intent. Disabling the feature releases connections and discovery while retaining selected devices for the next enable.

打开 **高级设置 → 实验性无线接收**。先在 Windows 蓝牙设置配对手机，再启用 A2DP、点击 **应用开关**，选中设备并点击 **连接并自动恢复**。最多记住八台设备，不承诺同时播放。**断开并取消恢复** 会取消该设备的开机重连；总开关关闭时释放连接和设备监视器，保留设备选择供下次启用。

Windows performs decoding and playback. Set the default Windows playback device to ChannelBridge's selected source, normally **CABLE Input**, to feed its existing routing. Stereo wireless input is not automatically upmixed to surround. The API does not expose a PCM stream or a selectable destination to this connector.

声音由 Windows 解码和播放。若要进入 ChannelBridge 路由，请将系统默认播放设备设为软件所选音源，通常是 **CABLE Input**。无线立体声不会自动扩展成环绕声；此连接 API 不提供直接 PCM 音频输入或输出设备选择。

Connections have a 30-second cancellation deadline and retry with backoff up to 120 seconds. A Windows denial pauses reconnect until a new configuration/retry request. Session 0 / LocalService compatibility depends on Windows and the Bluetooth stack; an error or empty service discovery must not be interpreted as successful receiving. A user-session companion may be necessary on systems that reject the API from a service; this build does not silently start such a companion.

连接设置了 30 秒取消期限，失败后逐步放宽重试间隔，最多 120 秒。Windows 拒绝访问时暂停重试，需点击刷新 / 重试。Session 0 / LocalService 是否支持取决于 Windows 和蓝牙驱动；错误或服务发现列表为空都不能视为接收成功。如果系统只允许用户会话访问，后续可能需要会话辅助进程，本版未自动启用此类降级方式。

## AirPlay adapter

The **AirPlay receiver settings** button manages a separately installed [UxPlayEnhanced](https://github.com/Kylepossible/UxPlayEnhanced/releases) engine. Reviewed interface: v1.1.1 / source commit 77b88a7b05c67c7d4a388d2fe8fe15c8078b564d. Install/extract its complete package to a location readable by LocalService, select **uxplay.exe**, choose a receiver name, then enable and apply. Keep its `lib/gstreamer-1.0` directory and DLLs. Do not launch its tray receiver simultaneously. Configure the engine's firewall access on the private network according to its instructions; ChannelBridge does not change firewall rules or install the engine automatically.

**AirPlay 接收设置** 管理另行安装的 UxPlayEnhanced 接收引擎。请安装/解压完整包到 LocalService 可读取的位置，选择其中的 **uxplay.exe**，填写名称后启用并应用。保留依赖 DLL 和 `lib/gstreamer-1.0` 目录，不要同时启动引擎自带的托盘程序。私有网络防火墙设置按引擎说明处理；ChannelBridge 不会自动修改防火墙或安装第三方引擎。

The service launches audio-only mode (`-vs 0`), uses a named stop event and parent-process watchdog, and attaches a Windows job that terminates the receiver if the service exits. Unexpected exits retry every 30 seconds; five consecutive failures pause until Apply / Retry. A minute of continuous process operation resets the failure streak. Changes to the executable hash require reselection/apply. **Running** only confirms a live process, not discovery, phone connectivity or playback. Receivers advertise to the local network when enabled; use a trusted private network.

服务以纯音频模式启动引擎，使用停止事件、父进程监视和 Windows Job 清理进程。意外退出后每 30 秒重试，连续五次失败后暂停，需手动应用 / 重试；进程持续运行一分钟后清零连续失败计数。引擎 EXE 改变后需要重新选择并应用。**引擎运行中** 只说明进程存活，不代表网络发现、手机连接和播放成功。启用后会向局域网公布接收端，请在可信私有网络使用。

Since `1.2.0-experimental.2`, ChannelBridge passes a stable, instance-specific device ID (`-m`) and stores the receiver key in `C:\ProgramData\ChannelBridge\airplay-receiver.pem`. This prevents it from sharing the physical NIC identity with a separate UxPlay receiver on the same computer. A local `/info` check verified distinct identities; discovery and playback still require verification from a phone. If the receiver is missing, refresh the phone's AirPlay audio output list, check that both devices share a LAN, and check multicast isolation and the engine's firewall access.

从 `1.2.0-experimental.2` 起，ChannelBridge 使用独立且重启后稳定的设备 ID，并在 `C:\ProgramData\ChannelBridge\airplay-receiver.pem` 保存接收端密钥，避免与同一电脑上另一个 UxPlay 接收端共用网卡标识。本机 `/info` 查询已验证标识不同，手机端发现及播放仍需实测。若列表中找不到接收端，请刷新 AirPlay 音频输出列表，检查双方是否在同一局域网，以及路由器组播隔离和引擎防火墙权限。

AirPlay now explicitly uses `wasapisink` and the source endpoint in the saved routing preset. Changing that endpoint restarts the receiver; changing speaker gains or delays does not. If no routing preset exists, WASAPI uses its default endpoint. This avoids implicit `autoaudiosink` selection, which selected `wasapi2` and reported an incompatible stream format during an iPhone ALAC session on the test PC. Phone playback with this sink has been confirmed for music and another video source; Bilibili app playback still fails.

AirPlay 现已明确使用 `wasapisink`，输出到已应用预设的音源端点。切换音源会重启接收端，单独调整音箱增益或延迟不会中断 AirPlay；没有路由预设时才使用 WASAPI 默认端点。此次 iPhone ALAC 会话中，原有 `autoaudiosink` 自动选择 `wasapi2` 后报告音频流格式不兼容。新的输出方式已确认音乐和其他视频来源可播放，哔哩哔哩 App 仍无声。

**Windows discovery:** the service now also registers AirPlay and RAOP through Windows DNS-SD, using the live receiver port and public metadata. It selects active Ethernet/Wi-Fi interfaces with an IPv4 gateway, uses the Windows hostname, and refreshes registrations when interface addresses change. Apply / Retry, receiver restart and disable retire the old registrations. No temporary script or UxPlayEnhanced UI is needed, and no adapter is disabled. Registration and iPhone discovery/music playback have been verified on the test PC; other network environments still require testing.

**Windows 发现修复：** 后台服务现已通过 Windows 原生 DNS-SD 持续注册 AirPlay 和 RAOP，读取当前引擎的实际端口及公开信息。自动选择具有 IPv4 网关的活动以太网 / Wi-Fi 网卡，使用 Windows 主机名，网卡地址变化时更新记录。应用 / 重试、引擎重启和关闭功能时会撤销旧记录。无需临时脚本或打开 UxPlayEnhanced 界面，不会禁用任何网卡。测试电脑已验证注册成功、iPhone 可发现且音乐有声；其他网络环境仍需验证。

## Chromecast

Not implemented. Google's supported Web Receiver runs on Cast devices; general Windows receiving is not provided by that SDK. Experimental independent receivers face device-certificate validation by normal sender apps. No credentials or certificate bypasses are bundled. References: [Google Cast overview](https://developers.google.com/cast/docs/overview), [independent receiver limitations](https://github.com/yukarikaname/openchromecast).

尚未实现。官方 Web Receiver 面向 Cast 设备；独立 Windows 接收端会遇到普通发送端的设备证书认证要求。界面明确标为不可用，没有伪装成可用的启用开关。

## Performance and stability evaluation / 后续性能与稳定性评估

- Keep routing devices, source playback, sample rates, buffers and service priority identical. Restart the service before each measurement to reset averages. Measure disabled baseline, discovery idle, A2DP playback, AirPlay playback, then both enabled if relevant. Use five minutes after warmup for each CPU/memory sample and export reports.
- A2DP panel CPU is the **entire ChannelBridge service process**, normalized over all logical CPUs. Windows Bluetooth decoding/audio processing is excluded; also observe Windows audio processes when evaluating total A2DP cost. AirPlay panel CPU measures **only the receiver process**, separately from ChannelBridge. Zero or low connector CPU does not establish low total decoding cost.
- Run at least one hour of playback per protocol; record audible glitches, attempts, failures, disconnects, unexpected exits, routing underruns, memory and handles. Repeat phone disconnect/reconnect, adapter removal, sleep/wake, UI closure, service restart and Windows reboot. Compare handles/memory after at least twenty reconnect cycles.
- Reports contain device display names, errors and timestamps; opaque Bluetooth identifiers are omitted from A2DP exports. No PCM, microphone audio or media files are recorded. In-memory event histories are capped at 64 entries; status files are replaced, not appended. Connection counters reset when that device's task is recreated; AirPlay process CPU averages reset when the receiver restarts. Current, mean and peak CPU are process measurements, not per-thread profiling.

固定路由、播放内容、采样率、缓冲和优先级，分别比较关闭基线、仅发现、A2DP 播放和 AirPlay 播放。每种情况先重启服务清零均值，预热后记录五分钟 CPU/内存，再进行至少一小时连续播放和二十次断连恢复。A2DP 面板统计整个 ChannelBridge 服务，**不包含 Windows 蓝牙解码进程**；AirPlay 面板单独统计接收引擎。导出报告用于对比，不录制音频。日志最多保留 64 条，状态文件循环替换。设备任务重建会重置连接计数，引擎重启会重置其 CPU 均值。

## Automated verification

`--wireless-check` uses simulated Bluetooth discovery and connections, including late completion, cancellation, denial, reconnect, corrupt configuration and disabled-state resource release. `--airplay-check` checks arguments, hash validation, real helper-process start/stop and Windows job cleanup. The helper is not an AirPlay receiver. Existing DSP, mapping, volume, calibration and Chinese/English UI regression checks remain in the packaging workflow. No successful acoustic or phone interoperability claim is made by these tests.

