# ChannelBridge 1.0 — Multi-device surround routing

Run `app/ChannelBridge.exe` on Windows x64. The runtime is included.

## Language

Use the **文 / A** language button immediately to the left of Minimize to switch between Chinese and English. The choice is remembered per Windows user in `%LocalAppData%\ChannelBridge\language.txt`. Switching does not change routing, presets or the background service. Windows device names remain unchanged.

## Connect speakers

1. Select **CABLE Input** as the playback source. On first run, if it is missing, the app opens the official VB-CABLE download page after showing installation instructions: https://vb-audio.com/Cable/.
2. Choose a speaker layout, then select each speaker on the map and assign its output device and L/R channel.
3. Click **Apply preset** to update the Windows source speaker layout and save routing, or **Start routing** to save and start playback. Administrator access is requested when needed.
4. Set Windows or your player to output to the selected virtual source. **System sound settings** opens the Windows sound control panel.

Supported layouts: 2.0, 2.1, 3.0, 3.1, 4.0, 4.1, 5.0, 5.1 side, 5.1 rear, 6.0, 6.1, 7.0 and 7.1. The app routes existing channels; it does not upmix stereo, synthesize LFE, add a crossover, or decode Dolby/DTS.

**Auto-map** matches channel positions using the source's actual Windows channel mask. It hides the manual Source channel setting while enabled. Device assignments, L/R, gain and delay are separate. Gain is -60 to +6 dB; delay is 0–500 ms.

## Volume and tests

**System volume: Follow** follows the selected source's Windows volume and mute. If the virtual driver already applies this volume to captured samples, select Bypass to avoid applying it twice. Physical output-device volumes are unchanged.

**Test speaker** plays the selected speaker; **Test preset** plays assigned, unmuted speakers in order. Tests use the current preset's gain and delay. Routing pauses for the test and resumes afterwards.

## Microphone latency

Place a microphone at the listening position, pause other audio, disable monitoring and keep volume unchanged. Click **Measure latency**, select the microphone, then **Apply and measure**.

Each active speaker is measured three times with temporary zero delay. The median sets compensation relative to the slowest speaker, then three verification trials pass through the actual compensated routing path. Each triplet must have spread ≤10 ms and mean-to-median difference ≤5 ms. Across speakers, verified means and medians must both have spread ≤5 ms. Only a complete, valid result is saved. A successful test asks whether to close the result window; choose No to continue comparing trials.

Invalid signals, clipping, ambiguous echoes, unreliable timestamps, unstable trials, cancellation or failed alignment do not save new compensation. The prior routing state is restored. The app keeps captured audio in memory only; it does not save recordings. Last results are stored in `%ProgramData%\ChannelBridge\calibration-result.json`.

Measured latency includes acoustic propagation and microphone-device latency and is intended for relative alignment. Independent devices may drift; this does not provide hardware clock synchronization.

## Background service and presets

The `ChannelBridge.Audio` service starts automatically and reads `%ProgramData%\ChannelBridge\config.json`. Closing the UI does not stop playback. Rebooting restores the saved run/stop intent. Missing devices are retried without silently switching assignments. **Stop routing** persists the stopped state.

**Save preset** exports a JSON preset. **Load preset** edits the current preset; click Apply to save it to the service. Applying a layout may briefly interrupt playback; some players need to restart playback after their output format changes.

Windows layout synchronization uses an undocumented audio-policy interface and validates the result. Other Windows/driver versions can reject it; errors are reported and rollback is attempted. Third-party licenses are in `app/licenses`.

## Low latency mode

Enable “Low latency (50ms buffer)” at the top, then apply the configuration. Off preserves the standard mode. Existing profiles default to standard mode. Saved profiles, service restarts and microphone calibration use the selected mode.

| Mode | Capture buffer request | Routing queue target | Output buffer request |
| --- | --- | --- | --- |
| Standard | 100ms (poll roughly every 50ms) | 80ms + speaker compensation | 40ms |
| Low latency | 25ms (poll at roughly half the actual buffer duration) | 50ms + speaker compensation | 20ms |

50ms is the routing queue's base target, not end-to-end latency. Drivers may adjust actual buffer sizes. VB-CABLE internal buffering, Windows mixing, device latency and acoustic travel time still contribute. This option does not change VB-CABLE's global internal buffer settings. Recalibrate speaker delays after switching modes. If audio breaks up, turn low latency off and apply the configuration. Advanced → Audio buffers in the top-right menu accepts individual capture/output values of 10–500ms and routing values of 10–1000ms. Confirm, then Apply in the main window. Reset restores 25/50/20ms. Legacy 40ms presets migrate to 50ms; explicit custom values are preserved. Smaller values are more prone to crackling and may not be supported by every driver.

## Native multichannel outputs and quick mapping

Route a 7.1 source to a native 5.1 device plus a stereo device without upmixing. Device selection shows the current Windows driver channel count and sample rate, not maximum advertised capability. Configure the desired layout in Windows/the device driver first.

Select an output device and enable **Use device channels** to expose its speaker positions (FL, FR, FC, LFE, surrounds, etc.). Off sends stereo L/R only. Mode applies to every speaker assigned to that device.

Selecting a multichannel device or enabling native mode offers a quick-map confirmation; **Quick map…** also opens it. The preview lists target positions, assignments that would be replaced, and incompatible old assignments that would be removed. Confirm, then click **Apply**. Unmatched positions on other devices remain available for the extra stereo pair. Side and rear positions are matched exactly, never interchanged in a 7.1 quick map.

Per-channel gain, delay, mute, test playback and microphone calibration follow the same mapping. Unassigned channels remain silent. Unknown driver positions require manual CH-number assignment. Changed driver layouts block stale mappings until refreshed and remapped. The stream preserves the Windows channel mask; driver effects/remixing and physical connections can still affect playback. Stereo mode does not disable a driver's own upmixing.

Saved profiles use schema 3 and accept older profiles on import; older software cannot read schema 3. Back up profiles before upgrading. Independent device clocks still need compensation and cannot guarantee hardware-level synchronization. Live driver checks use zero output gain; verify actual speaker wiring using test playback.

## Background service priority

Open **Advanced settings → Process priority (background service)** and choose Low, Below normal, Normal (default), Above normal or High. Click **Apply** to change the service process priority. The UI process is unaffected. The selection is saved with the preset and restored when the service or computer restarts. Older presets use Normal.

Priority affects scheduling under CPU contention. It does not change audio buffer values or guarantee crackle-free playback. Start with Normal; try Above normal if needed. Realtime priority is not offered. If applying priority fails, the error is reported and the previous running configuration is retained.
