using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace VoiceInput;

public partial class MainWindow
{
    private readonly Stopwatch mediaElapsed = new();
    private readonly Stopwatch mediaPhase = new();
    private string mediaPhaseName = "";
    private string? localMedia;
    private string? mediaFolder;
    private MediaSource? previousMedia;
    private MediaResult? mediaCandidate;
    private IReadOnlyList<SubtitleSegment> rawMedia = [];
    private List<SubtitleSegment> mediaDraft = [];
    private long mediaRevision;
    private bool mediaDirty;
    private bool lastForceAudio;
    private string mediaLog = "";

    private sealed record MediaSource(Uri? Uri, string? File);

    private void InitializePages()
    {
        VideoUrl.TextChanged += (_, _) =>
        {
            localMedia = null;
            MediaFileLabel.Text = "";
            UpdateMediaActions();
        };
        elapsedTimer.Tick += (_, _) => UpdateElapsed();
        elapsedTimer.Start();
        Navigate(this, new RoutedEventArgs());
    }

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (VoicePage is null || MediaPage is null) return;
        bool media = MediaNavigation.IsChecked == true;
        VoicePage.Visibility = media ? Visibility.Collapsed : Visibility.Visible;
        MediaPage.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BeginVoiceTiming()
    {
        voicePhaseName = "";
        voicePhase.Reset();
        voiceElapsed.Restart();
    }

    private void SetVoicePhase(string phase)
    {
        if (voicePhase.IsRunning) log += PhaseLog(voicePhaseName, voicePhase);
        voicePhaseName = phase;
        voicePhase.Restart();
        SetStatus(phase);
        UpdateElapsed();
    }

    private void FinishVoiceTiming()
    {
        if (voicePhase.IsRunning) log += PhaseLog(voicePhaseName, voicePhase);
        voicePhase.Stop();
        voiceElapsed.Stop();
        log += PhaseLog("Total", voiceElapsed);
        LogButton.IsEnabled = true;
        UpdateElapsed();
    }

    private void UpdateElapsed()
    {
        if (voiceElapsed.Elapsed != TimeSpan.Zero) VoiceElapsed.Text = FormatElapsed(voiceElapsed, voicePhase);
        if (mediaElapsed.Elapsed != TimeSpan.Zero) MediaElapsed.Text = FormatElapsed(mediaElapsed, mediaPhase);
    }

    private string FormatElapsed(Stopwatch total, Stopwatch phase) => string.Format(T("总耗时 {0} · 本阶段 {1}"),
        Elapsed(total.Elapsed), Elapsed(phase.Elapsed));

    private static string Elapsed(TimeSpan elapsed) =>
        FormattableString.Invariant($"{(long)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");

    private static string PhaseLog(string phase, Stopwatch elapsed) =>
        FormattableString.Invariant($"{phase} · {elapsed.Elapsed.TotalSeconds:F2} s{Environment.NewLine}");

    private static string Normalize(string text, RecognitionOptions settings) => settings.Language == "en" ? text :
        ChineseOutput.Convert(text, settings.Traditional,
            settings.ProtectedTerms.Split(['|', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    private void AcceptVoice(string raw, RecognitionOptions settings, long revision)
    {
        SetVoicePhase("正在生成结果…");
        string normalized = Normalize(raw, settings);
        rawVoice = raw;
        VoiceRawButton.Visibility = Visibility.Visible;
        if (voiceDirty || voiceRevision != revision)
        {
            voiceCandidate = normalized;
            VoiceCandidateButton.Visibility = Visibility.Visible;
            SetStatus("新结果已就绪。你可以预览后替换草稿。");
        }
        else
        {
            ReplaceVoice(normalized);
            SetStatus("识别完成。你可以编辑或复制结果。");
        }
    }

    private void ReplaceVoice(string text)
    {
        updatingVoice = true;
        Result.Text = text;
        updatingVoice = false;
        voiceRevision++;
        voiceDirty = false;
        voiceCandidate = null;
        VoiceCandidateButton.Visibility = Visibility.Collapsed;
    }

    private void UseVoiceCandidate(object sender, RoutedEventArgs e)
    {
        if (voiceCandidate is { } text && Preview(text, confirm: true)) ReplaceVoice(text);
    }

    private void ShowRawVoice(object sender, RoutedEventArgs e) => Preview(rawVoice, confirm: false);
    private void CancelVoice(object sender, RoutedEventArgs e) => cancellation?.Cancel();

    private void ImportMedia(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = T("音视频文件") + "|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg;*.mp4;*.mkv;*.webm;*.mov|*.*|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        VideoUrl.Clear();
        localMedia = dialog.FileName;
        MediaFileLabel.Text = Path.GetFileName(localMedia);
        UpdateMediaActions();
    }

    private async void StartVideoTranscription(object sender, RoutedEventArgs e) => await StartMediaAsync(forceAudio: false);
    private async void ForceMediaAudio(object sender, RoutedEventArgs e) => await StartMediaAsync(forceAudio: true);
    private async void RetryMedia(object sender, RoutedEventArgs e) => await StartMediaAsync(lastForceAudio);

    private async Task StartMediaAsync(bool forceAudio)
    {
        if (videoBusy || closePending) return;
        Uri? uri = null;
        if (localMedia is null && !VideoSource.TryParsePublicUri(VideoUrl.Text, out uri))
        {
            SetVideoStatus("请输入公开的 YouTube 或 B 站 http/https 链接。");
            return;
        }
        var media = new MediaSource(uri, localMedia);
        var options = ReadOptions(validatePaths: false) with
        {
            Language = (string)((ComboBoxItem)MediaLanguage.SelectedItem).Tag,
            Traditional = MediaTraditional.IsChecked == true
        };
        videoBusy = true;
        using var session = new CancellationTokenSource();
        videoCancellation = session;
        lastForceAudio = forceAudio;
        long revision = mediaRevision;
        mediaLog = "";
        mediaPhaseName = "";
        mediaPhase.Reset();
        mediaElapsed.Restart();
        SetMediaPhase(new("正在准备媒体…"));
        UpdateOperationState();
        var progress = new Progress<TranscriptionProgress>(value =>
        {
            if (videoCancellation == session && !session.IsCancellationRequested) SetMediaPhase(value);
        });
        try
        {
            if (previousMedia != media || mediaFolder is null)
            {
                DeleteTemporaryFolder(mediaFolder);
                mediaFolder = Directory.CreateTempSubdirectory("VoiceInput-media-").FullName;
                previousMedia = media;
            }
            var transcriber = new VideoTranscriber(FindToolsDirectory(), mediaFolder, recognitionQueue);
            var result = await transcriber.TranscribeAsync(media.Uri, media.File, options.Language, forceAudio,
                () => ValidateLocalOptions(options), progress, session.Token);
            session.Token.ThrowIfCancellationRequested();
            var normalized = await Task.Run(() => NormalizeMedia(result, options, session.Token), session.Token);
            session.Token.ThrowIfCancellationRequested();
            rawMedia = result.Segments;
            MediaRawButton.Visibility = Visibility.Visible;
            if (mediaDirty || revision != mediaRevision)
            {
                mediaCandidate = normalized;
                MediaCandidateButton.Visibility = Visibility.Visible;
                SetVideoStatus("新结果已就绪。你可以预览后替换草稿。");
            }
            else ReplaceMedia(normalized);
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            SetVideoStatus("转写已取消。");
        }
        catch (Exception error)
        {
            mediaLog += $"{mediaPhaseName} · {error.GetType().Name}{Environment.NewLine}";
            ShowOperationError(error, video: true);
        }
        finally
        {
            if (mediaPhase.IsRunning) mediaLog += PhaseLog(mediaPhaseName, mediaPhase);
            mediaPhase.Stop();
            mediaElapsed.Stop();
            mediaLog += PhaseLog("Total", mediaElapsed);
            videoCancellation = null;
            videoBusy = false;
            UpdateElapsed();
            UpdateOperationState();
            if (closePending) _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private static MediaResult NormalizeMedia(MediaResult result, RecognitionOptions options, CancellationToken token)
    {
        var segments = new List<SubtitleSegment>(result.Segments.Count);
        foreach (var segment in result.Segments)
        {
            token.ThrowIfCancellationRequested();
            segments.Add(new(segment.Start, segment.End, Normalize(segment.Text, options)));
        }
        return result with { Segments = segments };
    }

    private static RecognitionOptions ValidateLocalOptions(RecognitionOptions options)
    {
        if (!File.Exists(options.Engine) || !Path.GetFileName(options.Engine).Equals("whisper-cli.exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("请选择 whisper-cli.exe。");
        if (!File.Exists(options.Model)) throw new FileNotFoundException("请选择本地多语种模型文件。");
        return options with { Engine = Path.GetFullPath(options.Engine), Model = Path.GetFullPath(options.Model) };
    }

    private void SetMediaPhase(TranscriptionProgress progress)
    {
        if (mediaPhaseName != progress.Message)
        {
            if (mediaPhase.IsRunning) mediaLog += PhaseLog(mediaPhaseName, mediaPhase);
            mediaPhaseName = progress.Message;
            mediaPhase.Restart();
        }
        SetVideoStatus(progress.Message, progress.Percent is double percent ? [percent] : []);
        TranscriptionProgressBar.IsIndeterminate = progress.Percent is null;
        if (progress.Percent is double value) TranscriptionProgressBar.Value = Math.Clamp(value, 0, 100);
        UpdateElapsed();
    }

    private void CancelVideoTranscription(object sender, RoutedEventArgs e) => videoCancellation?.Cancel();

    private void ReplaceMedia(MediaResult result)
    {
        mediaDraft = result.Segments;
        MediaSegments.ItemsSource = mediaDraft;
        mediaRevision++;
        mediaDirty = false;
        mediaCandidate = null;
        MediaCandidateButton.Visibility = Visibility.Collapsed;
        SetVideoStatus("转写完成 · {0}", T(result.Source));
        UpdateMediaActions();
    }

    private void MediaEditing(object sender, DataGridBeginningEditEventArgs e)
    {
        mediaRevision++;
        mediaDirty = true;
    }

    private void UseMediaCandidate(object sender, RoutedEventArgs e)
    {
        if (mediaCandidate is { } candidate && Preview(MediaText(candidate.Segments), confirm: true)) ReplaceMedia(candidate);
    }

    private void ShowRawMedia(object sender, RoutedEventArgs e) => Preview(MediaText(rawMedia), confirm: false);

    private void UpdateMediaActions()
    {
        ImportMediaButton.IsEnabled = !videoBusy;
        RetryMediaButton.IsEnabled = !videoBusy && previousMedia is not null &&
            (localMedia is not null ? previousMedia.File == localMedia :
                VideoSource.TryParsePublicUri(VideoUrl.Text, out var uri) && previousMedia.Uri == uri);
        ForceAudioButton.IsEnabled = !videoBusy && VideoSource.TryParsePublicUri(VideoUrl.Text, out _);
        CopyMediaButton.IsEnabled = SaveMediaTxtButton.IsEnabled = SaveMediaSrtButton.IsEnabled = mediaDraft.Count > 0;
        MediaLogButton.IsEnabled = mediaLog.Length > 0;
    }

    private void CopyMedia(object sender, RoutedEventArgs e)
    {
        MediaSegments.CommitEdit(DataGridEditingUnit.Row, true);
        try { Clipboard.SetText(MediaText(mediaDraft)); SetVideoStatus("已复制当前结果，可手动粘贴。"); }
        catch (ExternalException) { SetVideoStatus("剪贴板正被占用，请重试。"); }
    }

    private static string MediaText(IEnumerable<SubtitleSegment> segments) =>
        string.Join(Environment.NewLine, segments.Select(segment => segment.Text));

    private void SaveMediaTxt(object sender, RoutedEventArgs e) => SaveMedia(srt: false);
    private void SaveMediaSrt(object sender, RoutedEventArgs e) => SaveMedia(srt: true);

    private void SaveMedia(bool srt)
    {
        if (!MediaSegments.CommitEdit(DataGridEditingUnit.Row, true)) return;
        string ext = srt ? ".srt" : ".txt";
        var dialog = new SaveFileDialog { Filter = srt ? "SRT (*.srt)|*.srt" : T("文本文件 (*.txt)") + "|*.txt",
            DefaultExt = ext, AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            string text = srt ? Subtitles.WriteSrt(mediaDraft) : MediaText(mediaDraft);
            if (string.IsNullOrWhiteSpace(text)) { SetVideoStatus("没有可导出的正文。"); return; }
            File.WriteAllText(dialog.FileName, text, new UTF8Encoding(false));
            mediaDirty = false;
            SetVideoStatus("已保存当前结果。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetVideoStatus("保存失败：{0}", error.Message);
        }
    }

    private void ShowMediaLog(object sender, RoutedEventArgs e) => Preview(mediaLog, confirm: false, title: T("识别日志"));

    private bool Preview(string text, bool confirm, string? title = null)
    {
        var dialog = new Window { Owner = this, Title = title ?? T("识别结果（可编辑）"),
            Width = 640, Height = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(12) };
        var button = new Button { Content = T(confirm ? "替换草稿" : "关闭"), Margin = new Thickness(0, 8, 0, 0) };
        button.Click += (_, _) => dialog.DialogResult = true;
        DockPanel.SetDock(button, Dock.Bottom);
        panel.Children.Add(button);
        panel.Children.Add(new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = panel;
        return dialog.ShowDialog() == true;
    }

    private static string FindToolsDirectory() => Path.Combine(AppContext.BaseDirectory, "tools");

    private static void DeleteTemporaryFolder(string? folder)
    {
        if (folder is null) return;
        try { Directory.Delete(folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
