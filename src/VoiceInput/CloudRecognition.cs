using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;

namespace VoiceInput;

internal static class CloudRecognition
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };

    internal static async Task<string> RunAsync(string key, string model, string audio, string language,
        CancellationToken token, HttpClient? client = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("请填写 OpenRouter API Key。");
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("请填写语音转写模型名称。");
        var body = new Dictionary<string, object>
        {
            ["model"] = model.Trim(),
            ["input_audio"] = new { data = Convert.ToBase64String(await File.ReadAllBytesAsync(audio, token)), format = "wav" }
        };
        if (language != "auto") body["language"] = language;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/audio/transcriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        request.Content = JsonContent.Create(body);
        using var response = await (client ?? Client).SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenRouter 识别失败（HTTP {(int)response.StatusCode}）。请检查 Key、余额、模型及网络。");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!json.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("OpenRouter 未返回有效转写文字。");
        return text.GetString()!.Trim();
    }
}

internal static class KeyProtection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public nint Data; }

    internal static string Protect(string value) => Transform(value, true);
    internal static string Unprotect(string value) => Transform(value, false);

    private static string Transform(string value, bool protect)
    {
        if (value.Length == 0) return "";
        byte[] bytes = protect ? Encoding.UTF8.GetBytes(value) : Convert.FromBase64String(value);
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect
                ? CryptProtectData(ref input, null, 0, 0, 0, 1, out output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            byte[] result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return protect ? Convert.ToBase64String(result) : Encoding.UTF8.GetString(result);
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != 0) LocalFree(output.Data);
            Array.Clear(bytes);
        }
    }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
