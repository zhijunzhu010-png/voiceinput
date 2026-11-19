# VoiceInput V0.2

[English](README.en.md)

适用于 Windows 10/11 x64 的本地语音输入工具。界面使用 C# / WPF，麦克风录音以 16 kHz、16 位、单声道 WAV 交给本机 whisper.cpp 转写。配置好程序、引擎和模型后，可以完全断网运行。

## 使用发行 ZIP 安装

1. 将 ZIP 完整解压到普通目录，例如 `C:\VoiceInput`。打开 `app\VoiceInput.exe`，其中已附带 .NET 运行时。保留同级的 `engine-cpu` 和 `models` 目录。
2. 首次启动会自动选中随包附带的 whisper.cpp v1.8.3 CPU 版和多语种 `ggml-tiny.bin`。保持 CUDA 复选框关闭，即可离线使用。已保存配置的电脑若显示旧路径，可手动浏览到上述目录。
3. 如需提高识别质量，可另行下载 [ggml-base.bin](https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin) 或 [ggml-small.bin](https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin)，在界面中选择模型。中英及混合模式使用多语种模型。文件名带 `.en` 的模型仅适用于英文。
4. 要启用 NVIDIA 加速，请按安装教程配置独立的 CUDA 引擎。可在联网电脑备齐所需文件，再复制到离线电脑。
5. 点击“开始录音”或按 **Ctrl+Shift+F9**，讲完后主动结束录音。在结果区预览、编辑或复制文字，也可以打开“更多选项”后选择“确认输入”。
6. 使用顶部下拉栏切换 English、简体中文或繁體中文。新配置默认识别普通话、输出简体。可独立设置识别语言和输出繁简，并用 `|` 分隔需要保留原字形的专名。“自动 / 中英混合”由 Whisper 检测主要语言。

从左侧进入「链接及文件转文字」处理公开链接或本地音视频。两个页面各自保留输入框、草稿与任务。切页时任务继续运行。识别完成时如果已有未保存编辑，可先预览新结果，再决定是否替换草稿。操作与导出见 [链接与文件转写](docs/VIDEO_TRANSCRIPTION.md)。

繁简输出使用随程序嵌入的 OpenCC 1.1.9 字符及词组词典。转换离线执行，数据许可证随程序发布。 [OpenCC 1.1.9 源码](https://github.com/BYVoid/OpenCC/tree/ver.1.1.9)

## 自动输入与复制

自动输入前会比对录音时捕获的前台窗口及 Windows 焦点句柄。程序通过 Unicode 键盘事件发送文字，将换行转成空格以免触发提交。某些应用、管理员窗口和自绘编辑器可能拒绝或忽略模拟输入。Windows 只能报告输入事件是否送入队列，无法确认目标编辑器是否收到全部文字。观察目标窗口后再决定是否点击“复制结果”，或选中结果按 Ctrl+C。复制保留原始换行。

录音和引擎输出位于本次会话的系统临时目录，常规结束时会清理。强制结束程序后可能需要清理系统临时目录中的 `VoiceInput-*`。设置保存在 `%LOCALAPPDATA%\VoiceInput\settings.json`，识别结果显示在窗口中。快捷键注册冲突时，可关闭占用者并重启 VoiceInput，或使用界面按钮。

## 从源码编译

安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。在 PowerShell 进入 ZIP 解压后的根目录运行。首次还原 NuGet 依赖需要联网或已有本地缓存。仓库提供项目源码。源码发布后，请从 whisper.cpp 官方发布页下载 Windows x64 CPU 引擎，并从模型页下载多语种模型，然后在应用中浏览选择。

```powershell
dotnet build .\src\VoiceInput\VoiceInput.csproj -c Release
dotnet publish .\src\VoiceInput\VoiceInput.csproj -c Release -r win-x64 --self-contained true -o .\app
```

源码位于 `src\VoiceInput`。入口为 `App.xaml`，窗口与录音流程在 `MainWindow.xaml` 和 `MainWindow.xaml.cs`。录音、识别进程和 Windows 输入分别由 `AudioCapture.cs`、`WhisperEngine.cs` 和 `DesktopInput.cs` 负责。麦克风录音依赖 [NAudio.WinMM](https://www.nuget.org/packages/NAudio.WinMM/2.2.1/)。

CUDA 安装、自行构建与故障排查见 [安装教程.md](安装教程.md)。设备验收步骤见 [验收.md](验收.md)。

CPU 引擎来自 [whisper.cpp v1.8.3 官方发布](https://github.com/ggml-org/whisper.cpp/releases/tag/v1.8.3)。附带的 [多语种 tiny 模型](https://huggingface.co/ggerganov/whisper.cpp/blob/main/ggml-tiny.bin) 的 SHA-256 为 `be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21`。
