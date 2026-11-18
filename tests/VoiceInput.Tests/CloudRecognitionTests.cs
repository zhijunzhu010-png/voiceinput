using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VoiceInput.Tests;

[TestClass]
public class CloudRecognitionTests
{
    [TestMethod]
    public void KeyIsEncryptedAndCanBeRecovered()
    {
        string encrypted = KeyProtection.Protect("test-secret");
        Assert.AreNotEqual("test-secret", encrypted);
        Assert.AreEqual("test-secret", KeyProtection.Unprotect(encrypted));
    }

    [TestMethod]
    public async Task SendsAudioAndReadsTranscript()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
            using var client = new HttpClient(new Handler(async request =>
            {
                Assert.AreEqual("https://openrouter.ai/api/v1/audio/transcriptions", request.RequestUri!.ToString());
                Assert.AreEqual("Bearer test-key", request.Headers.Authorization!.ToString());
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.AreEqual("test-model", body.RootElement.GetProperty("model").GetString());
                Assert.AreEqual("AQID", body.RootElement.GetProperty("input_audio").GetProperty("data").GetString());
                Assert.AreEqual("zh", body.RootElement.GetProperty("language").GetString());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"  中文 test  \"}") };
            }));
            Assert.AreEqual("中文 test", await CloudRecognition.RunAsync("test-key", "test-model", path, "zh", default, client));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task HttpErrorDoesNotExposeResponseSecrets()
    {
        string path = Path.GetTempFileName();
        try
        {
            using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            { Content = new StringContent("secret-from-provider") })));
            var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                CloudRecognition.RunAsync("test-key", "test-model", path, "auto", default, client));
            StringAssert.Contains(error.Message, "401");
            Assert.IsFalse(error.Message.Contains("secret-from-provider"));
        }
        finally { File.Delete(path); }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => run(request);
    }
}
