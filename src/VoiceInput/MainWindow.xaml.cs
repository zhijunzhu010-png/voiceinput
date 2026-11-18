using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;

namespace VoiceInput;

public partial class MainWindow : Window
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceInput", "settings.json");
    private HwndSource? source;
    private AudioCapture? capture;
    private CancellationTokenSource? cancellation;
    private CancellationTokenSource? videoCancellation;
    private bool busy, videoBusy, stopping, closePending, registered;
    private InputTarget target;
    private string log = "";
    private string? recordingFolder;
    private bool confirming;
    private static readonly string CloudSettingsPath = Path.ChangeExtension(SettingsPath, "cloud.json");
    private sealed record CloudSettings(string ProtectedKey, string Model, int Provider);

    public MainWindow()
    {
        InitializeComponent();
        string bundledEngine = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "engine-cpu", "whisper-cli.exe"));
        if (File.Exists(bundledEngine)) EnginePath.Text = bundledEngine;
        string bundledModel = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "models", "ggml-tiny.bin"));
        if (File.Exists(bundledModel)) ModelPath.Text = bundledModel;
        InitializeInterfaceLanguage();
        Result.TextChanged += ResultChanged;
        UpdateResultActions();
        try
        {
            if (File.Exists(SettingsPath) && JsonSerializer.Deserialize<RecognitionOptions>(File.ReadAllText(SettingsPath)) is { } settings)
            {
                EnginePath.Text = settings.Engine;
                ModelPath.Text = settings.Model;
                LanguagePicker.SelectedIndex = settings.Language switch { "zh" => 1, "en" => 2, _ => 0 };
                UseGpu.IsChecked = settings.Gpu;
            }
        }
        catch (Exception ex) { SetStatus("读取配置失败：{0}", ex.Message); }
        try
        {
            if (File.Exists(CloudSettingsPath) && JsonSerializer.Deserialize<CloudSettings>(File.ReadAllText(CloudSettingsPath)) is { } cloud)
            {
                ApiKey.Password = KeyProtection.Unprotect(cloud.ProtectedKey);
                CloudModel.Text = cloud.Model;
                ProviderPicker.SelectedIndex = cloud.Provider == 1 ? 1 : 0;
            }
        }
        catch { SetStatus("无法读取云端配置，请重新填写 Key。"); }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WindowMessage);
        registered = DesktopInput.Register(source.Handle);
        if (!registered) SetStatus("快捷键注册失败。可使用窗口按钮，或释放 Ctrl+Shift+F9 后重启。");
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == DesktopInput.HotkeyMessage && wParam == DesktopInput.HotkeyId)
        {
            handled = true;
            Dispatcher.BeginInvoke(new Action(() => Toggle(fromHotkey: true)));
        }
        return 0;
    }

    private void ToggleFromButton(object sender, RoutedEventArgs e) => Toggle(fromHotkey: false);

    private void Toggle(bool fromHotkey)
    {
        if (closePending || videoBusy || confirming) return;
        if (!busy)
        {
            target = fromHotkey ? DesktopInput.Capture() : default;
            if (target.Window == source?.Handle) target = default;
            _ = RunSessionAsync();
        }
        else if (capture is not null && !stopping)
        {
            if (!fromHotkey || target != DesktopInput.Capture()) target = default;
            StopCapture();
        }
    }

    private void StopCapture()
    {
        if (capture is null || stopping) return;
        stopping = true;
        UpdateOperationState();
        SetStatus("正在结束录音…");
        try { capture.Stop(); }
        catch (Exception ex)
        {
            log = ex.ToString();
            cancellation?.Cancel();
        }
    }

    private async Task RunSessionAsync()
    {
        busy = true;
        log = "";
        LogButton.IsEnabled = false;
        stopping = false;
        UpdateOperationState();
        using var session = new CancellationTokenSource();
        cancellation = session;
        string? folder = null;
        try
        {
            ValidateRecognition();
            ClearRecording();
            folder = Directory.CreateTempSubdirectory("VoiceInput-").FullName;
            recordingFolder = folder;
            string wav = Path.Combine(folder, "recording.wav");
            capture = new AudioCapture(wav);
            capture.Start();
            UpdateOperationState();
            SetStatus("正在录音…");
            await capture.Completion.WaitAsync(session.Token);
            capture.Dispose();
            capture = null;
            RecordButton.IsEnabled = false;
            if (new FileInfo(wav).Length < 3244) throw new InvalidOperationException("录音太短，请重试。");

            var result = await RecognizeAsync(wav, session.Token);
            log = result.Log;
            if (string.IsNullOrWhiteSpace(result.Text))
            {
                SetStatus("本次没有识别到文字。");
                return;
            }
            Result.Text = result.Text;
            SetStatus("识别完成。你可以编辑或复制结果。");
        }
        catch (OperationCanceledException)
        {
            if (!closePending) SetStatus("录音已中断。请查看识别日志。");
        }
        catch (Exception ex)
        {
            ShowOperationError(ex);
        }
        finally
        {
            try { capture?.Dispose(); } catch (Exception ex) { log += $"\n释放麦克风失败：{ex.Message}"; }
            capture = null;
            cancellation = null;
            busy = false;
            LogButton.IsEnabled = log.Length != 0;
            UpdateOperationState();
            if (closePending) _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void ValidateRecognition()
    {
        if (ProviderPicker.SelectedIndex == 1)
        {
            if (string.IsNullOrWhiteSpace(ApiKey.Password) || string.IsNullOrWhiteSpace(CloudModel.Text))
                throw new InvalidOperationException("请填写 OpenRouter Key 和转写模型。");
        }
        else ReadOptions();
    }

    private async Task<(string Text, string Log)> RecognizeAsync(string wav, CancellationToken token)
    {
        ValidateRecognition();
        if (ProviderPicker.SelectedIndex == 1)
        {
            SetStatus("正在上传录音并通过 OpenRouter 识别…");
            SaveCloudConfiguration();
            string language = (string)((ComboBoxItem)LanguagePicker.SelectedItem).Tag;
            return (await CloudRecognition.RunAsync(ApiKey.Password, CloudModel.Text, wav, language, token), "OpenRouter 转写完成。");
        }
        SetStatus("正在本地识别…");
        var settings = ReadOptions();
        SaveSettings(settings);
        return await WhisperEngine.RunAsync(settings, wav, token);
    }

    private void SaveCloudConfiguration()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CloudSettingsPath)!);
        File.WriteAllText(CloudSettingsPath, JsonSerializer.Serialize(new CloudSettings(
            KeyProtection.Protect(ApiKey.Password), CloudModel.Text.Trim(), ProviderPicker.SelectedIndex)));
    }

    private void SaveCloudSettings(object sender, RoutedEventArgs e)
    {
        try { SaveCloudConfiguration(); SetStatus("云端配置已保存，Key 已使用 Windows 加密。"); }
        catch (Exception ex) { SetStatus("保存失败：{0}", ex.Message); }
    }

    private async void RetryRecognition(object sender, RoutedEventArgs e)
    {
        if (busy || videoBusy || confirming || recordingFolder is null) return;
        busy = true;
        using var session = new CancellationTokenSource();
        cancellation = session;
        UpdateOperationState();
        try
        {
            var result = await RecognizeAsync(Path.Combine(recordingFolder, "recording.wav"), session.Token);
            Result.Text = result.Text;
            log = result.Log;
            SetStatus("重新识别完成，请预览后确认输入。");
        }
        catch (OperationCanceledException) { SetStatus("识别已取消。"); }
        catch (Exception ex) { ShowOperationError(ex); }
        finally
        {
            busy = false;
            cancellation = null;
            UpdateOperationState();
            if (closePending) Close();
        }
    }

    private async void ConfirmInput(object sender, RoutedEventArgs e)
    {
        if (busy || videoBusy || confirming || string.IsNullOrWhiteSpace(Result.Text)) return;
        string text = Result.Text;
        confirming = true;
        UpdateOperationState();
        try
        {
            for (int seconds = 3; seconds > 0; seconds--)
            {
                SetStatus("请切换到目标应用并点击输入框，{0} 秒后输入…", seconds);
                await Task.Delay(1000);
                if (closePending) return;
            }
            var destination = DesktopInput.Capture();
            SetStatus(destination.Window != source?.Handle && DesktopInput.TryType(destination, text)
                ? "已发送文字输入，不会自动提交；请核对目标应用。"
                : "未完成输入，请重新确认或复制后粘贴。");
        }
        finally { confirming = false; UpdateOperationState(); }
    }

    private void ClearRecording()
    {
        if (recordingFolder is null) return;
        try { Directory.Delete(recordingFolder, true); recordingFolder = null; }
        catch (IOException ex) { log += $"\n录音清理失败：{ex.Message}"; }
    }

    private async void StartVideoTranscription(object sender, RoutedEventArgs e)
    {
        if (busy || videoBusy || closePending || confirming) return;
        if (!VideoSource.TryParsePublicUri(VideoUrl.Text, out Uri? uri))
        {
            SetVideoStatus("请输入公开的 YouTube 或 B 站 http/https 链接。");
            return;
        }

        RecognitionOptions settings;
        try
        {
            settings = ReadOptions();
            SaveSettings(settings);
        }
        catch (Exception ex)
        {
            ShowOperationError(ex, video: true);
            return;
        }

        videoBusy = true;
        videoCancellation = new CancellationTokenSource();
        TranscriptionProgressBar.IsIndeterminate = true;
        TranscriptionProgressBar.Value = 0;
        UpdateOperationState();
        var progress = new Progress<TranscriptionProgress>(value =>
        {
            if (!videoBusy || videoCancellation?.IsCancellationRequested != false) return;
            if (value.Stage == "下载视频音频" && value.Percent.HasValue &&
                value.Message != "正在获取公开视频音频…")
                SetVideoStatus("正在下载音频 {0:0.#}%", value.Percent.Value);
            else
                SetVideoStatus(value.Message);
            TranscriptionProgressBar.IsIndeterminate = value.Percent is null;
            if (value.Percent is double percent) TranscriptionProgressBar.Value = Math.Clamp(percent, 0, 100);
        });
        try
        {
            var result = await VideoTranscriber.TranscribeAsync(uri!, settings, FindToolsDirectory(), progress, videoCancellation.Token);
            log = result.Log;
            if (string.IsNullOrWhiteSpace(result.Text)) SetVideoStatus("视频没有识别到文字。");
            else
            {
                Result.Text = result.Text;
                SetVideoStatus("视频转写完成。结果可继续编辑。");
            }
        }
        catch (OperationCanceledException)
        {
            if (!closePending) SetVideoStatus("视频转写已取消，临时文件已清理。");
        }
        catch (Exception ex)
        {
            ShowOperationError(ex, video: true);
        }
        finally
        {
            videoCancellation.Dispose();
            videoCancellation = null;
            videoBusy = false;
            LogButton.IsEnabled = log.Length != 0;
            UpdateOperationState();
            if (closePending) _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void CancelVideoTranscription(object sender, RoutedEventArgs e)
    {
        if (!videoBusy) return;
        SetVideoStatus("正在取消视频转写…");
        videoCancellation?.Cancel();
    }

    private static string FindToolsDirectory()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "tools");
        if (File.Exists(Path.Combine(bundled, "yt-dlp.exe")) || File.Exists(Path.Combine(bundled, "ffmpeg.exe"))) return bundled;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "tools"));
    }

    private RecognitionOptions ReadOptions()
    {
        string engine = EnginePath.Text.Trim();
        string model = ModelPath.Text.Trim();
        if (!File.Exists(engine) || !string.Equals(Path.GetFileName(engine), "whisper-cli.exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("请选择 whisper-cli.exe。", engine);
        if (!File.Exists(model)) throw new FileNotFoundException("请选择本地多语种模型文件。", model);
        return new(Path.GetFullPath(engine), Path.GetFullPath(model),
            (string)((ComboBoxItem)LanguagePicker.SelectedItem).Tag, UseGpu.IsChecked == true);
    }

    private static void SaveSettings(RecognitionOptions settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
    }

    private void BrowseEngine(object sender, RoutedEventArgs e) => Browse(EnginePath, "whisper-cli.exe|whisper-cli.exe");
    private void BrowseModel(object sender, RoutedEventArgs e) => Browse(ModelPath, T("Whisper 模型 (*.bin)") + "|*.bin");

    private void Browse(TextBox field, string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) field.Text = dialog.FileName;
    }

    private void ResultChanged(object sender, TextChangedEventArgs e) => UpdateResultActions();

    private void UpdateResultActions()
    {
        bool hasResult = !string.IsNullOrWhiteSpace(Result?.Text);
        bool enabled = hasResult && !busy && !videoBusy && !confirming;
        ConfirmButton.IsEnabled = enabled;
        RetryButton.IsEnabled = recordingFolder is not null && !busy && !videoBusy && !confirming;
        CopyButton.IsEnabled = enabled;
        ClearButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
    }

    private void UpdateOperationState()
    {
        UpdateRecordLabel();
        RecordButton.IsEnabled = !videoBusy && !confirming && (!busy || capture is not null && !stopping);
        ProviderPicker.IsEnabled = !busy && !videoBusy && !confirming;
        ApiKey.IsEnabled = ProviderPicker.IsEnabled;
        CloudModel.IsEnabled = ProviderPicker.IsEnabled;
        TranscribeButton.IsEnabled = !busy && !videoBusy && !confirming;
        CancelVideoButton.IsEnabled = videoBusy;
        CancelVideoButton.Visibility = videoBusy ? Visibility.Visible : Visibility.Collapsed;
        TranscriptionProgressBar.Visibility = CancelVideoButton.Visibility;
        VideoUrl.IsEnabled = !busy && !videoBusy && !confirming;
        UpdateResultActions();
    }

    private void CopyResult(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Result.Text)) return;
        try
        {
            Clipboard.SetText(Result.Text);
            SetStatus("已复制当前结果，可手动粘贴。");
        }
        catch (ExternalException) { SetStatus("剪贴板正被占用，请重试。"); }
    }

    private void ClearResult(object sender, RoutedEventArgs e)
    {
        Result.Clear();
        SetStatus("已清空识别结果。");
    }

    private void SaveResult(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Result.Text)) return;
        var dialog = new SaveFileDialog { Filter = T("文本文件 (*.txt)") + "|*.txt", DefaultExt = ".txt", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, Result.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            SetStatus("已保存当前结果。");
        }
        catch (Exception ex) { SetStatus("保存失败：{0}", ex.Message); }
    }

    private void ShowLog(object sender, RoutedEventArgs e)
    {
        var details = new TextBox { Text = log, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12) };
        new Window { Owner = this, Title = T("识别日志"), Width = 720, Height = 440, Content = details,
            WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || (!busy && !videoBusy)) return;
        e.Cancel = true;
        closePending = true;
        StopCapture();
        videoCancellation?.Cancel();
        cancellation?.Cancel();
    }

    protected override void OnClosed(EventArgs e)
    {
        closePending = true;
        ClearRecording();
        if (source is not null)
        {
            if (registered) DesktopInput.Unregister(source.Handle);
            source.RemoveHook(WindowMessage);
        }
        base.OnClosed(e);
    }
}
