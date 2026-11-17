using System.Diagnostics;
using System.IO;
using System.Text;

namespace VoiceInput;

internal sealed record RecognitionOptions(string Engine, string Model, string Language, bool Gpu);

internal static class WhisperEngine
{
    public static async Task<(string Text, string Log)> RunAsync(
        RecognitionOptions options, string audio, CancellationToken cancellationToken)
    {
        string output = Path.Combine(Path.GetDirectoryName(audio)!, "result");
        var start = new ProcessStartInfo(options.Engine)
        {
            WorkingDirectory = Path.GetDirectoryName(options.Engine)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in new[]
        {
            "-m", options.Model, "-f", audio, "-l", options.Language, "-otxt", "-of", output, "-nt"
        })
            start.ArgumentList.Add(arg);
        if (!options.Gpu) start.ArgumentList.Add("-ng");

        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动识别引擎。");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        string log = await stderr;
        if (process.ExitCode != 0 || !File.Exists(output + ".txt"))
            throw new InvalidOperationException($"识别失败，退出码 {process.ExitCode}（0x{process.ExitCode:X8}）。\n{log}\n{await stdout}");
        return ((await File.ReadAllTextAsync(output + ".txt", Encoding.UTF8, cancellationToken)).Trim(), log);
    }
}
