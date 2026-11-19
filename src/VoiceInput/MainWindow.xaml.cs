using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
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
    private string log = "";
    private readonly LocalRecognitionQueue recognitionQueue = new();
    private readonly DispatcherTimer elapsedTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch voiceElapsed = new();
    private readonly Stopwatch voicePhase = new();
    private string voicePhaseName = "";
    private string rawVoice = "";
    private string? voiceCandidate;
    private long voiceRevision;
    private bool voiceDirty;
    private bool updatingVoice;
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
        InitializePages();
        UpdateOperationState();
        try
        {
            if (File.Exists(SettingsPath) && JsonSerializer.Deserialize<RecognitionOptions>(File.ReadAllText(SettingsPath)) is { } settings)
            {
                if (File.Exists(settings.Engine)) EnginePath.Text = settings.Engine;
                if (File.Exists(settings.Model)) ModelPath.Text = settings.Model;
                LanguagePicker.SelectedIndex = settings.Language switch { "zh" => 1, "en" => 2, _ => 0 };
                UseGpu.IsChecked = settings.Gpu;
                TraditionalOutput.IsChecked = settings.Traditional;
                ProtectedTerms.Text = settings.ProtectedTerms ?? "";
            }
        }
        catch (Exception ex) { SetStatus("读取配置失败：{0}", ex.GetType().Name); }
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
        if (closePending || confirming) return;
        if (!busy)
        {
            _ = RunSessionAsync();
        }
        else if (capture is not null && !stopping)
        {
            StopCapture();
        }
    }

    private void StopCapture()
    {
        if (capture is null || stopping) return;
        stopping = true;
        UpdateOperationState();
        SetVoicePhase("正在结束录音…");
        try { capture.Stop(); }
        catch (Exception ex)
        {
            log += $"{ex.GetType().Name}{Environment.NewLine}";
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
        var revision = voiceRevision;
        BeginVoiceTiming();
        try
        {
            var request = ReadVoiceRequest();
            folder = Directory.CreateTempSubdirectory("VoiceInput-").FullName;
            string wav = Path.Combine(folder, "recording.wav");
            capture = new AudioCapture(wav);
            capture.Start();
            UpdateOperationState();
            SetVoicePhase("正在录音…");
            await capture.Completion.WaitAsync(session.Token);
            capture.Dispose();
            capture = null;
            UpdateOperationState();
            if (new FileInfo(wav).Length < 3244) throw new InvalidOperationException("录音太短，请重试。");

            ClearRecording();
            recordingFolder = folder;
            var result = await RecognizeAsync(wav, request, session.Token);
            if (string.IsNullOrWhiteSpace(result))
            {
                SetStatus("本次没有识别到文字。");
                return;
            }
            AcceptVoice(result, request.Options, revision);
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
            try { capture?.Dispose(); } catch (Exception ex) { log += $"\n释放麦克风失败 · {ex.GetType().Name}"; }
            capture = null;
            if (folder is not null && folder != recordingFolder) DeleteTemporaryFolder(folder);
            FinishVoiceTiming();
            cancellation = null;
            busy = false;
            LogButton.IsEnabled = log.Length != 0;
            UpdateOperationState();
            if (closePending) _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private sealed record VoiceRequest(RecognitionOptions Options, bool Cloud, string Key, string Model);

    private VoiceRequest ReadVoiceRequest()
    {
        bool cloud = ProviderPicker.SelectedIndex == 1;
        var options = ReadOptions(validatePaths: !cloud);
        if (cloud && (string.IsNullOrWhiteSpace(ApiKey.Password) || string.IsNullOrWhiteSpace(CloudModel.Text)))
            throw new InvalidOperationException("请填写 OpenRouter Key 和转写模型。");
        return new(options, cloud, ApiKey.Password, CloudModel.Text);
    }

    private async Task<string> RecognizeAsync(string wav, VoiceRequest request, CancellationToken token)
    {
        SaveSettings(request.Options);
        if (request.Cloud)
        {
            SetVoicePhase("正在上传录音并通过 OpenRouter 识别…");
            SaveCloudConfiguration();
            return await CloudRecognition.RunAsync(request.Key, request.Model, wav, request.Options.Language, token);
        }

        return await recognitionQueue.RunAsync(voice: true, () => SetVoicePhase("正在等待本地识别…"), async () =>
        {
            SetVoicePhase("正在本地识别…");
            var result = await WhisperEngine.RunAsync(request.Options, wav, token);
            log += result.Log + Environment.NewLine;
            return result.Text;
        }, token);
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
        if (busy || confirming || recordingFolder is null) return;
        busy = true;
        using var session = new CancellationTokenSource();
        cancellation = session;
        UpdateOperationState();
        long revision = voiceRevision;
        log = "";
        BeginVoiceTiming();
        try
        {
            var request = ReadVoiceRequest();
            var result = await RecognizeAsync(Path.Combine(recordingFolder, "recording.wav"), request, session.Token);
            if (string.IsNullOrWhiteSpace(result)) SetStatus("本次没有识别到文字。");
            else AcceptVoice(result, request.Options, revision);
        }
        catch (OperationCanceledException) { SetStatus("识别已取消。"); }
        catch (Exception ex) { ShowOperationError(ex); }
        finally
        {
            FinishVoiceTiming();
            busy = false;
            cancellation = null;
            UpdateOperationState();
            if (closePending) _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private async void ConfirmInput(object sender, RoutedEventArgs e)
    {
        if (busy || confirming || string.IsNullOrWhiteSpace(Result.Text)) return;
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
        DeleteTemporaryFolder(recordingFolder);
        recordingFolder = null;
    }

    private RecognitionOptions ReadOptions(bool validatePaths = true)
    {
        string engine = EnginePath.Text.Trim();
        string model = ModelPath.Text.Trim();
        var options = new RecognitionOptions(engine, model, (string)((ComboBoxItem)LanguagePicker.SelectedItem).Tag,
            UseGpu.IsChecked == true, TraditionalOutput.IsChecked == true, ProtectedTerms.Text);
        return validatePaths ? ValidateLocalOptions(options) : options;
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

    private void ResultChanged(object sender, TextChangedEventArgs e)
    {
        if (!updatingVoice)
        {
            voiceRevision++;
            voiceDirty = true;
        }
        UpdateResultActions();
    }

    private void UpdateResultActions()
    {
        bool hasResult = !string.IsNullOrWhiteSpace(Result?.Text);
        bool enabled = hasResult && !busy && !confirming;
        ConfirmButton.IsEnabled = enabled;
        RetryButton.IsEnabled = recordingFolder is not null && !busy && !confirming;
        CopyButton.IsEnabled = enabled;
        ClearButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
    }

    private void UpdateOperationState()
    {
        UpdateRecordLabel();
        RecordButton.IsEnabled = !confirming && (!busy || capture is not null && !stopping);
        ProviderPicker.IsEnabled = !busy && !confirming;
        ApiKey.IsEnabled = ProviderPicker.IsEnabled;
        CloudModel.IsEnabled = ProviderPicker.IsEnabled;
        CancelVideoButton.IsEnabled = videoBusy;
        CancelVideoButton.Visibility = videoBusy ? Visibility.Visible : Visibility.Collapsed;
        TranscriptionProgressBar.Visibility = CancelVideoButton.Visibility;
        TranscribeButton.IsEnabled = !videoBusy;
        VideoUrl.IsEnabled = !videoBusy;
        MediaLanguage.IsEnabled = !videoBusy;
        MediaTraditional.IsEnabled = !videoBusy;
        VoiceCancel.Visibility = busy && capture is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateMediaActions();
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
            voiceDirty = false;
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
        if (e.Cancel || (!busy)) return;
        e.Cancel = true;
        closePending = true;
        StopCapture();
        videoCancellation?.Cancel();
        cancellation?.Cancel();
    }

    protected override void OnClosed(EventArgs e)
    {
        closePending = true;
        elapsedTimer.Stop();
        ClearRecording();
        DeleteTemporaryFolder(mediaFolder);
        if (source is not null)
        {
            if (registered) DesktopInput.Unregister(source.Handle);
            source.RemoveHook(WindowMessage);
        }
        base.OnClosed(e);
    }
}
