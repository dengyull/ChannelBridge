# Third-party notices

ChannelBridge's source is MIT licensed. Dependencies and the bundled self-contained runtime retain their own licenses.

- NAudio 2.2.1: MIT; `licenses/NAudio.txt`. https://github.com/naudio/NAudio
- System.ServiceProcess.ServiceController 10.0.6 and .NET runtime: MIT; `licenses/DOTNET.txt`, `licenses/DOTNET-THIRD-PARTY.txt`, and `licenses/ServiceController-THIRD-PARTY-NOTICES.txt`. https://github.com/dotnet/runtime
- Windows Desktop runtime: MIT with third-party notices; `licenses/WindowsDesktop.txt`. https://github.com/dotnet/winforms

The build additionally copies license/notice files from the resolved Windows runtime packs into the distribution, so the packaged runtime's notices accompany the binaries.

VB-CABLE is not bundled. Download it from https://vb-audio.com/Cable/ and follow its separate license and installation terms. ChannelBridge is not affiliated with VB-Audio.

Experimental wireless receiving:

- The A2DP connection lifecycle follows the public Windows API demonstrated by AudioPlaybackConnector, MIT, copyright 2020 Richard Yu: https://github.com/ysc3839/AudioPlaybackConnector (reviewed commit f1975592ef346cad2a2d8384efe41c50ea194d2f). Its license is retained in `licenses/AudioPlaybackConnector.txt`.
- C#/WinRT runtime: MIT, Microsoft, `licenses/CsWinRT.txt`; https://github.com/microsoft/CsWinRT. Microsoft.Windows.SDK.NET.Ref 10.0.19041.57 supplies Windows API projections; Windows SDK terms are included as `licenses/WindowsSDK.rtf` (https://aka.ms/WinSDKLicenseURL).
- UxPlayEnhanced is an optional, separately installed GPL-3.0 receiver with additional dependency licenses: https://github.com/Kylepossible/UxPlayEnhanced. No UxPlay, GStreamer, FFmpeg or AirPlay protocol implementation is linked or redistributed in this package. ChannelBridge manages the independently installed executable through command-line arguments and its documented shutdown mechanism. Preserve its complete distribution and license notices when installing it.
- AirPlay, Bluetooth and Chromecast names identify interoperability targets, not certification or affiliation. Chromecast receiving is not implemented.
