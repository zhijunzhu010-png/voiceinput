# VoiceInput V0.1

[简体中文](README.md)

VoiceInput is an offline dictation app for Windows 10 and 11 (x64). The C# and WPF app records 16 kHz, 16-bit mono audio, sends it to a local whisper.cpp process, and attempts to type the transcript into the active application. Once the engine and model are in place, transcription works without an internet connection or an API key.

## Get started

1. If you have the distribution ZIP, extract it and open `app\VoiceInput.exe`. Keep the `app`, `engine-cpu`, and `models` folders together. The ZIP includes the .NET runtime, CPU engine, and multilingual tiny model. If you cloned this repository, follow the build instructions below.
2. Open a text field and press **Ctrl+Shift+F9** to start recording. Press it again to stop. Recordings are limited to about two minutes.
3. Keep focus in the same text field until transcription finishes. If text does not appear, use **Copy result** (`复制结果`) in VoiceInput and paste it manually. The app window also has a recording button for use without the global shortcut.

The app UI is in Chinese. The language choices are Auto / mixed Chinese and English (`自动 / 中英混合`), Chinese (`中文`), and English (`英文`). Auto detects the primary language. A multilingual model can transcribe both languages in one recording, with accuracy depending on the model, audio quality, and language switching. Use a multilingual model such as `ggml-tiny.bin`, `ggml-base.bin`, or `ggml-small.bin`. Models with `.en` in their names are English-only. [Whisper models](https://huggingface.co/ggerganov/whisper.cpp/tree/main)

## Build from source

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), then run these commands from the repository root in PowerShell. The first NuGet restore requires network access or a local package cache.

```powershell
dotnet build .\src\VoiceInput\VoiceInput.csproj -c Release
dotnet publish .\src\VoiceInput\VoiceInput.csproj -c Release -r win-x64 --self-contained true -o .\app
```

Download the Windows x64 CPU engine and a multilingual model separately. Keep the engine DLLs beside `whisper-cli.exe`. In VoiceInput, browse to the executable and the model file. [whisper.cpp v1.8.3 releases](https://github.com/ggml-org/whisper.cpp/releases/tag/v1.8.3) · [Download ggml-base.bin](https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin)

## NVIDIA CUDA

Use a Windows x64 CUDA build of whisper.cpp and install an NVIDIA driver and runtime libraries compatible with that build. Put the CUDA engine in a separate directory, select its `whisper-cli.exe` in VoiceInput, and check **Enable NVIDIA CUDA** (`启用 NVIDIA CUDA`). Run a transcription and open the log (`识别日志`) to confirm which backend the engine selected. See the Chinese [installation guide](安装教程.md) for prebuilt packages, CMake build commands, and troubleshooting.

## Troubleshooting and verification

- Enable microphone access for desktop apps in Windows settings and check the default input device. If the shortcut is already in use, use the recording button or release the shortcut and restart VoiceInput.
- Automatic input sends Unicode keyboard events and replaces line breaks with spaces. Some apps, including elevated windows, may ignore those events. Copying the result preserves the original line breaks.
- Settings are saved in `%LOCALAPPDATA%\VoiceInput\settings.json`. The app normally deletes temporary audio and transcription files after each session.
- The Release build and self-contained x64 publish succeeded. A CPU smoke test transcribed synthetic English audio in English and Auto modes. Live microphone input, Chinese and mixed-language audio, cross-window input, and CUDA inference are listed for testing on the target device. [Verification record](验证记录.md) · [Manual checklist](验收.md)
