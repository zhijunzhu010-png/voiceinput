using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;

namespace VoiceInput;

internal sealed record TranscriptionProgress(string Message, double? Percent = null);
internal sealed record MediaResult(List<SubtitleSegment> Segments, string Source);

internal sealed class VideoTranscriber(string tools, string folder, LocalRecognitionQueue queue)
{
    private static readonly Regex DownloadPercent = new(@"\[download\]\s+(?<percent>\d+(?:\.\d+)?)%", RegexOptions.Compiled);

    internal async Task<MediaResult> TranscribeAsync(Uri? uri, string? localFile, string language,
        bool forceAudio, Func<RecognitionOptions> getSettings, IProgress<TranscriptionProgress> progress, CancellationToken token)
    {
        string wav = Path.Combine(folder, "audio.wav");
        if (!File.Exists(wav))
        {
            string? input = localFile;
            if (uri is not null)
            {
                string downloader = RequireTool("yt-dlp.exe");
                input = Directory.GetFiles(folder, "download.*").FirstOrDefault(IsCompleteDownload);
                if (!forceAudio && input is null)
                {
                    progress.Report(new("正在获取视频信息…"));
                    var metadata = await RunProcessAsync(downloader,
                        ["--ignore-config", "--no-playlist", "--skip-download", "--dump-single-json", "--", uri.AbsoluteUri],
                        _ => { }, token);
                    if (metadata.ExitCode != 0) throw new InvalidOperationException("无法访问链接，请检查网络或视频访问权限。");
                    progress.Report(new("正在检查字幕…"));
                    using var json = JsonDocument.Parse(metadata.Output);
                    var track = SelectTrack(json.RootElement, language);
                    if (track is { } selected)
                    {
                        var subtitles = await DownloadSubtitlesAsync(downloader, uri, selected.Language, selected.Automatic, token);
                        if (subtitles.Count > 0)
                        {
                            progress.Report(new("正在生成结果…"));
                            return new(subtitles, selected.Automatic ? "平台自动字幕" : "平台人工字幕");
                        }
                    }
                }

                if (input is null)
                {
                    progress.Report(new("正在获取公开视频音频…"));
                    var download = await RunProcessAsync(downloader,
                        BuildDownloadArguments(uri, Path.Combine(folder, "download.%(ext)s")), line =>
                        {
                            var match = DownloadPercent.Match(line);
                            if (match.Success && double.TryParse(match.Groups["percent"].Value,
                                NumberStyles.Number, CultureInfo.InvariantCulture, out var percent))
                                progress.Report(new("正在下载音频 {0:0.#}%", percent));
                        }, token);
                    if (download.ExitCode != 0)
                        throw new InvalidOperationException("音频下载失败，请检查网络或确认视频提供独立音轨。");
                    input = Directory.GetFiles(folder, "download.*").FirstOrDefault(IsCompleteDownload);
                }
            }

            if (input is null || !File.Exists(input)) throw new InvalidOperationException("未找到可转写的音频文件。");
            progress.Report(new("正在转换为 Whisper 音频格式…"));
            if (IsWhisperWav(input)) wav = input;
            else
            {
                string temporary = Path.Combine(folder, "preparing.wav");
                var conversion = await RunProcessAsync(RequireTool("ffmpeg.exe"),
                    BuildFfmpegArguments(input, temporary), _ => { }, token);
                if (conversion.ExitCode != 0 || !IsWhisperWav(temporary))
                    throw new InvalidOperationException("无法读取音轨，请检查文件格式或文件是否损坏。");
                File.Move(temporary, wav, overwrite: true);
            }
        }

        var settings = getSettings();
        var result = await queue.RunAsync(voice: false, () => progress.Report(new("正在等待本地识别…")), async () =>
        {
            progress.Report(new("正在使用本地 Whisper 引擎识别…"));
            return await WhisperEngine.RunAsync(settings, wav, token, timed: true, outputDirectory: folder);
        }, token);
        progress.Report(new("正在生成结果…"));
        var segments = Subtitles.Parse(result.Text, stripMarkup: false);
        if (segments.Count == 0) throw new InvalidOperationException("没有识别到可用字幕。");
        return new(segments, "本地 Whisper");
    }

    private async Task<List<SubtitleSegment>> DownloadSubtitlesAsync(string downloader, Uri uri,
        string language, bool automatic, CancellationToken token)
    {
        string template = Path.Combine(folder, "captions.%(ext)s");
        foreach (var path in Directory.GetFiles(folder, "captions.*")) File.Delete(path);
        var result = await RunProcessAsync(downloader,
            ["--ignore-config", "--no-playlist", "--skip-download", automatic ? "--write-auto-subs" : "--write-subs",
            "--sub-lang", "^" + Regex.Escape(language) + "$", "--sub-format", "vtt/srt", "-o", template, "--", uri.AbsoluteUri],
            _ => { }, token);
        if (result.ExitCode != 0) return [];
        var file = Directory.GetFiles(folder, "captions.*").FirstOrDefault(path =>
            Path.GetExtension(path) is ".vtt" or ".srt");
        return file is null ? [] : Subtitles.Parse(await File.ReadAllTextAsync(file, token));
    }

    internal static (string Language, bool Automatic)? SelectTrack(JsonElement metadata, string language)
    {
        if (language == "auto")
        {
            if (!metadata.TryGetProperty("language", out var source) || source.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(source.GetString()))
                return null;
            language = source.GetString()!;
        }
        foreach (string property in new[] { "subtitles", "automatic_captions" })
        {
            if (!metadata.TryGetProperty(property, out var tracks) || tracks.ValueKind != JsonValueKind.Object) continue;
            var track = tracks.EnumerateObject().Where(entry =>
                (entry.Name.Equals(language, StringComparison.OrdinalIgnoreCase) ||
                 entry.Name.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase)) &&
                entry.Value.ValueKind == JsonValueKind.Array &&
                entry.Value.EnumerateArray().Any(format => format.TryGetProperty("ext", out var ext) &&
                    ext.GetString() is "vtt" or "srt" &&
                    (property != "automatic_captions" || !format.TryGetProperty("url", out var url) ||
                     url.GetString()?.Contains("tlang=", StringComparison.OrdinalIgnoreCase) != true)))
                .OrderByDescending(entry => entry.Name.Equals(language, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Name).FirstOrDefault();
            if (track is not null) return (track, property == "automatic_captions");
        }
        return null;
    }

    private static bool IsCompleteDownload(string path) =>
        !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) && new FileInfo(path).Length > 0;

    private static bool IsWhisperWav(string file)
    {
        if (!Path.GetExtension(file).Equals(".wav", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var reader = new WaveFileReader(file);
            return reader.Length > 0 && reader.WaveFormat.Encoding == WaveFormatEncoding.Pcm &&
                reader.WaveFormat.SampleRate == 16000 && reader.WaveFormat.BitsPerSample == 16 &&
                reader.WaveFormat.Channels == 1;
        }
        catch (Exception error) when (error is IOException or FormatException or ArgumentException) { return false; }
    }

    private string RequireTool(string tool)
    {
        string path = Path.Combine(tools, tool);
        if (!File.Exists(path)) throw new FileNotFoundException("Missing media tool.", path);
        return path;
    }

    internal static string[] BuildDownloadArguments(Uri uri, string outputTemplate) =>
        ["--ignore-config", "--no-playlist", "--newline", "--progress", "-f", "bestaudio", "-o", outputTemplate, "--", uri.AbsoluteUri];

    internal static string[] BuildFfmpegArguments(string input, string output) =>
        ["-nostdin", "-v", "error", "-y", "-i", input, "-map", "0:a:0", "-vn",
         "-ac", "1", "-ar", "16000", "-sample_fmt", "s16", output];

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string executable,
        IEnumerable<string> arguments,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"无法启动 {Path.GetFileName(executable)}。");
        var output = new StringBuilder();
        Task stdout = ReadLinesAsync(process.StandardOutput, output, onLine);
        Task stderr = ReadLinesAsync(process.StandardError, null, onLine);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            throw;
        }

        await Task.WhenAll(stdout, stderr);
        return (process.ExitCode, output.ToString());
    }

    private static async Task ReadLinesAsync(StreamReader reader, StringBuilder? output, Action<string> onLine)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            output?.AppendLine(line);
            onLine(line);
        }
    }
}
