using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VoiceInput;

public partial class MainWindow : Window
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceInput", "settings.json");
    private readonly DispatcherTimer limit = new() { Interval = TimeSpan.FromMinutes(2) };
    private HwndSource? source;
    private AudioCapture? capture;
    private CancellationTokenSource? cancellation;
    private bool busy, stopping, closePending, registered;
    private InputTarget target;
    private string log = "";

    public MainWindow()
    {
        InitializeComponent();
        string bundledEngine = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "engine-cpu", "whisper-cli.exe"));
        if (File.Exists(bundledEngine)) EnginePath.Text = bundledEngine;
        string bundledModel = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "models", "ggml-tiny.bin"));
        if (File.Exists(bundledModel)) ModelPath.Text = bundledModel;
        limit.Tick += (_, _) => StopCapture();
        try
        {
            if (File.Exists(SettingsPath) &&
                JsonSerializer.Deserialize<RecognitionOptions>(File.ReadAllText(SettingsPath)) is { } settings)
            {
                EnginePath.Text = settings.Engine;
                ModelPath.Text = settings.Model;
                LanguagePicker.SelectedIndex = settings.Language switch { "zh" => 1, "en" => 2, _ => 0 };
                UseGpu.IsChecked = settings.Gpu;
            }
        }
        catch (Exception ex) { Status.Text = $"读取配置失败：{ex.Message}"; }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WindowMessage);
        registered = DesktopInput.Register(source.Handle);
        if (!registered) Status.Text = "快捷键注册失败。可用窗口按钮录音，或释放 Ctrl+Shift+F9 后重启。";
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
        if (closePending) return;
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
        limit.Stop();
        RecordButton.IsEnabled = false;
        Status.Text = "正在结束录音…";
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
        using var session = new CancellationTokenSource();
        cancellation = session;
        string? folder = null;
        try
        {
            var settings = ReadOptions();
            try { SaveSettings(settings); } catch (Exception ex) { log = $"保存配置失败：{ex.Message}\n"; }
            folder = Directory.CreateTempSubdirectory("VoiceInput-").FullName;
            string wav = Path.Combine(folder, "recording.wav");

            capture = new AudioCapture(wav);
            capture.Start();
            limit.Start();
            RecordButton.Content = "结束录音";
            Status.Text = "正在录音（最长 2 分钟）…";
            await capture.Completion.WaitAsync(session.Token);
            limit.Stop();
            capture.Dispose();
            capture = null;
            RecordButton.IsEnabled = false;
            if (new FileInfo(wav).Length < 3244)
                throw new InvalidOperationException("录音太短，请重试。");

            Status.Text = "正在本地识别…";
            var result = await WhisperEngine.RunAsync(settings, wav, session.Token);
            log = result.Log;
            if (string.IsNullOrWhiteSpace(result.Text))
            {
                Status.Text = "本次没有识别到文字。";
                return;
            }

            Result.Text = result.Text;
            CopyButton.IsEnabled = true;
            await Task.Delay(180, session.Token);
            Status.Text = DesktopInput.TryType(target, result.Text)
                ? "已发送输入事件。若目标未显示文字，可点击“复制结果”。"
                : "未完成自动输入。请点击“复制结果”后手动粘贴。";
        }
        catch (OperationCanceledException)
        {
            if (!closePending) Status.Text = "录音已中断。请查看识别日志。";
        }
        catch (Exception ex)
        {
            log = ex.ToString();
            Status.Text = "录音或识别失败。请查看识别日志。";
        }
        finally
        {
            limit.Stop();
            try { capture?.Dispose(); }
            catch (Exception ex) { log += $"\n释放麦克风失败：{ex.Message}"; }
            capture = null;
            if (folder is not null)
            {
                try { Directory.Delete(folder, recursive: true); }
                catch (Exception ex) { log += $"\n清理临时目录失败：{folder}\n{ex.Message}"; }
            }
            cancellation = null;
            busy = false;
            RecordButton.Content = "开始录音";
            RecordButton.IsEnabled = true;
            LogButton.IsEnabled = log.Length != 0;
            if (closePending) _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private RecognitionOptions ReadOptions()
    {
        string engine = EnginePath.Text.Trim();
        string model = ModelPath.Text.Trim();
        if (!File.Exists(engine) || !string.Equals(Path.GetFileName(engine), "whisper-cli.exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("请选择 whisper-cli.exe。");
        if (!File.Exists(model)) throw new FileNotFoundException("请选择本地多语种模型文件。");
        return new(Path.GetFullPath(engine), Path.GetFullPath(model),
            (string)((ComboBoxItem)LanguagePicker.SelectedItem).Tag, UseGpu.IsChecked == true);
    }

    private static void SaveSettings(RecognitionOptions settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
    }

    private void BrowseEngine(object sender, RoutedEventArgs e) =>
        Browse(EnginePath, "whisper-cli.exe|whisper-cli.exe");

    private void BrowseModel(object sender, RoutedEventArgs e) =>
        Browse(ModelPath, "Whisper 模型 (*.bin)|*.bin");

    private void Browse(TextBox field, string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) field.Text = dialog.FileName;
    }

    private void CopyResult(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Result.Text);
            Status.Text = "已复制，可手动粘贴。";
        }
        catch (ExternalException)
        {
            Status.Text = "剪贴板正被占用。请重试或选中结果后按 Ctrl+C。";
        }
    }

    private void ShowLog(object sender, RoutedEventArgs e)
    {
        var details = new TextBox
        {
            Text = log, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12)
        };
        new Window
        {
            Owner = this, Title = "识别日志", Width = 720, Height = 440, Content = details,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        }.ShowDialog();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || !busy) return;
        e.Cancel = true;
        closePending = true;
        StopCapture();
        cancellation?.Cancel();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (source is not null)
        {
            if (registered) DesktopInput.Unregister(source.Handle);
            source.RemoveHook(WindowMessage);
        }
        base.OnClosed(e);
    }
}
