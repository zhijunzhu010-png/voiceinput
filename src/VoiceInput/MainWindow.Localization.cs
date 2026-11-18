using System.IO;
using System.Windows.Controls;

namespace VoiceInput;

public partial class MainWindow
{
    private static string InterfaceLanguagePath => Path.ChangeExtension(SettingsPath, "language.txt");
    private string interfaceLanguage = "zh-CN";
    private (string Template, object[] Arguments) status = ("就绪", []);
    private (string Template, object[] Arguments) videoStatus = ("视频转写就绪", []);

    private static readonly Dictionary<string, string> InterfaceResources = new()
    {
        ["DisplayLanguage"] = "界面语言",
        ["Heading"] = "说话，自动转成文字",
        ["RecordingHelp"] = "录音结束后自动转写。",
        ["Shortcut"] = "快捷键 Ctrl + Shift + F9",
        ["VideoLink"] = "公开视频链接（YouTube 或 B 站）",
        ["ResultTitle"] = "识别结果（可编辑）",
        ["MoreOptions"] = "更多选项",
        ["Provider"] = "识别方式",
        ["LocalProvider"] = "本地 Whisper",
        ["CloudProvider"] = "云端 OpenRouter",
        ["ApiKeyLabel"] = "OpenRouter API Key（仅云端使用，本机加密保存）",
        ["CloudModelHint"] = "OpenRouter 语音转写模型 ID",
        ["EngineLabel"] = "Whisper 引擎",
        ["ModelLabel"] = "多语种模型 (.bin)",
        ["SpokenLanguage"] = "识别语言",
        ["AutoLanguage"] = "自动 / 中英混合",
        ["ChineseLanguage"] = "中文",
        ["EnglishLanguage"] = "英文",
        ["UseCuda"] = "启用 NVIDIA CUDA",
        ["StartVideo"] = "开始转写",
        ["CancelVideo"] = "取消转写",
        ["Clear"] = "清空",
        ["Copy"] = "复制结果",
        ["SaveCloud"] = "保存云端设置",
        ["Browse"] = "浏览",
        ["Retry"] = "重新识别",
        ["Send"] = "确认输入（3 秒）",
        ["SaveText"] = "保存 TXT",
        ["Log"] = "识别日志",
    };

    private string T(string text) => InterfaceLanguage.Text(interfaceLanguage, text);

    private void InitializeInterfaceLanguage()
    {
        try
        {
            if (File.Exists(InterfaceLanguagePath))
                interfaceLanguage = File.ReadAllText(InterfaceLanguagePath).Trim();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        InterfaceLanguagePicker.SelectedIndex = interfaceLanguage switch
        {
            "en" => 0,
            "zh-TW" => 2,
            _ => 1
        };
        interfaceLanguage = (string)((ComboBoxItem)InterfaceLanguagePicker.SelectedItem).Tag;
        ApplyInterfaceLanguage();
        InterfaceLanguagePicker.SelectionChanged += ChangeInterfaceLanguage;
    }

    private void ChangeInterfaceLanguage(object sender, SelectionChangedEventArgs e)
    {
        if (InterfaceLanguagePicker.SelectedItem is not ComboBoxItem { Tag: string language }) return;
        interfaceLanguage = language;
        ApplyInterfaceLanguage();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InterfaceLanguagePath)!);
            File.WriteAllText(InterfaceLanguagePath, language);
        }
        catch (IOException) { SetStatus("界面语言无法保存。"); }
        catch (UnauthorizedAccessException) { SetStatus("界面语言无法保存。"); }
    }

    private void ApplyInterfaceLanguage()
    {
        foreach (var (key, text) in InterfaceResources)
            Resources[key] = T(text);

        UpdateRecordLabel();
        Status.Text = string.Format(T(status.Template), status.Arguments);
        VideoStatus.Text = string.Format(T(videoStatus.Template), videoStatus.Arguments);
    }

    private void UpdateRecordLabel() =>
        RecordButton.Content = T(capture is not null && !stopping ? "结束录音" : "开始录音");

    private void SetStatus(string template, params object[] arguments)
    {
        status = (template, arguments);
        Status.Text = string.Format(T(template), arguments);
    }

    private void SetVideoStatus(string template, params object[] arguments)
    {
        videoStatus = (template, arguments);
        VideoStatus.Text = string.Format(T(template), arguments);
    }

    private void ShowOperationError(Exception error, bool video = false)
    {
        log = error.ToString();
        LogButton.IsEnabled = true;
        if (video && error is FileNotFoundException missing &&
            Path.GetFileName(missing.FileName) is ("yt-dlp.exe" or "ffmpeg.exe") and var tool)
        {
            SetVideoStatus("未找到 {0}，请将工具放入 tools 目录。", tool);
            return;
        }

        string message = error.Message is "请选择 whisper-cli.exe。" or "请选择本地多语种模型文件。" or
            "请填写 OpenRouter Key 和转写模型。" or "录音太短，请重试。"
            ? error.Message : "操作失败，请查看识别日志。";
        if (video) SetVideoStatus(message);
        else SetStatus(message);
    }
}
