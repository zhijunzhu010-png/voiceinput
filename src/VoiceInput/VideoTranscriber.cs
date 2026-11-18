using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceInput;

internal sealed record TranscriptionProgress(string Stage, double? Percent, string Message);

internal static class VideoTranscriber
{
    private static readonly Regex DownloadPercent = new(@"\[download\]\s+(?<percent>\d+(?:\.\d+)?)%", RegexOptions.Compiled);

    public static async Task<(string Text, string Log)> TranscribeAsync(
        Uri uri,
        RecognitionOptions options,
        string toolsDirectory,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        string downloader = Path.Combine(toolsDirectory, "yt-dlp.exe");
        string ffmpeg = Path.Combine(toolsDirectory, "ffmpeg.exe");
        if (!File.Exists(downloader)) throw new FileNotFoundException("未找到 yt-dlp.exe，请将工具放入 tools 目录。", downloader);
        if (!File.Exists(ffmpeg)) throw new FileNotFoundException("未找到 ffmpeg.exe，请将工具放入 tools 目录。", ffmpeg);

        string folder = Directory.CreateTempSubdirectory("VoiceInput-video-").FullName;
        var log = new StringBuilder();
        try
        {
            string template = Path.Combine(folder, "download.%(ext)s");
            progress?.Report(new("下载视频音频", 0, "正在获取公开视频音频…"));
            var download = await RunProcessAsync(
                downloader,
                BuildDownloadArguments(uri, template),
                line =>
                {
                    log.AppendLine(line);
                    Match match = DownloadPercent.Match(line);
                    if (match.Success && double.TryParse(match.Groups["percent"].Value, out double percent))
                        progress?.Report(new("下载视频音频", percent, $"正在下载音频 {percent:0.#}%"));
                },
                cancellationToken);
            if (download.ExitCode != 0)
                throw new InvalidOperationException($"视频音频下载失败，退出码 {download.ExitCode}。\n{download.Output}");

            string? input = Directory.GetFiles(folder, "download.*")
                .FirstOrDefault(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
            if (input is null) throw new InvalidOperationException("下载完成但没有找到音频文件。");

            string wav = Path.Combine(folder, "audio.wav");
            progress?.Report(new("转换音频", null, "正在转换为 Whisper 音频格式…"));
            var convert = await RunProcessAsync(
                ffmpeg,
                BuildFfmpegArguments(input, wav),
                line => log.AppendLine(line),
                cancellationToken);
            if (convert.ExitCode != 0 || !File.Exists(wav))
                throw new InvalidOperationException($"音频转换失败，退出码 {convert.ExitCode}。\n{convert.Output}");

            progress?.Report(new("本地识别", null, "正在使用本地 Whisper 引擎识别…"));
            var result = await WhisperEngine.RunAsync(options, wav, cancellationToken);
            progress?.Report(new("完成", 100, "视频转写完成。"));
            return (result.Text, log.Append(result.Log).ToString());
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch { }
        }
    }

    internal static string[] BuildDownloadArguments(Uri uri, string outputTemplate) =>
    ["--no-playlist", "--newline", "--progress", "-f", "bestaudio/best", "-o", outputTemplate, uri.AbsoluteUri];

    internal static string[] BuildFfmpegArguments(string input, string output) =>
    ["-y", "-i", input, "-ac", "1", "-ar", "16000", "-sample_fmt", "s16", output];

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

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"无法启动 {Path.GetFileName(executable)}。");
        var output = new StringBuilder();
        Task stdout = ReadLinesAsync(process.StandardOutput, output, onLine);
        Task stderr = ReadLinesAsync(process.StandardError, output, onLine);
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

    private static async Task ReadLinesAsync(StreamReader reader, StringBuilder output, Action<string> onLine)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            output.AppendLine(line);
            onLine(line);
        }
    }
}
