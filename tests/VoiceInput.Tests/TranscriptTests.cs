using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace VoiceInput.Tests;

[TestClass]
public class TranscriptTests
{
    [TestMethod]
    public void VttRoundTripsAsEditableSrt()
    {
        var segments = Subtitles.Parse("WEBVTT\n\n00:01.250 --> 00:02.500 align:start\n<b>第一段</b>\n\n00:03.000 --> 00:04.000\nSecond\n");
        Assert.AreEqual(2, segments.Count);
        segments[0].Text = "已编辑 English 123";
        var srt = Subtitles.WriteSrt(segments);
        StringAssert.Contains(srt, "00:00:01,250 --> 00:00:02,500");
        Assert.AreEqual("已编辑 English 123", Subtitles.Parse(srt)[0].Text);
    }

    [TestMethod]
    public void NormalizationRetainsProtectedContent()
    {
        Assert.AreEqual("后台 123 GitHub `後臺` https://例子.cn/後臺",
            ChineseOutput.Convert("後臺 123 GitHub `後臺` https://例子.cn/後臺", false, []));
        Assert.AreEqual("臺灣软件", ChineseOutput.Convert("臺灣軟件", false, ["臺灣"]));
        Assert.AreEqual("後臺", ChineseOutput.Convert("后台", true, []));
    }

    [TestMethod]
    public void PrefersManualChineseCaptionOverTranslatedAndAutomaticTracks()
    {
        using var json = JsonDocument.Parse("""
            {"language":"zh","subtitles":{"en":[{"ext":"vtt"}],"zh-Hans":[{"ext":"srt"}]},
             "automatic_captions":{"zh":[{"ext":"vtt","url":"https://example.test?lang=en&tlang=zh"}]}}
            """);
        Assert.AreEqual(("zh-Hans", false), VideoTranscriber.SelectTrack(json.RootElement, "zh")!.Value);
        Assert.AreEqual(("zh-Hans", false), VideoTranscriber.SelectTrack(json.RootElement, "auto")!.Value);
    }

    [TestMethod]
    public async Task SpeechGetsPriorityAndCancelingOtherPageLeavesActiveWorkAlone()
    {
        var queue = new LocalRecognitionQueue();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = queue.RunAsync(false, () => Assert.Fail(), async () =>
        {
            await release.Task;
            return 7;
        }, default);
        using var cancel = new CancellationTokenSource();
        var discarded = queue.RunAsync(false, () => { }, () => Task.FromResult(0), cancel.Token);
        var order = new List<string>();
        var media = queue.RunAsync(false, () => { }, () => { order.Add("media"); return Task.FromResult(1); }, default);
        var voice = queue.RunAsync(true, () => { }, () => { order.Add("voice"); return Task.FromResult(2); }, default);
        cancel.Cancel();
        try
        {
            await discarded;
            Assert.Fail("Canceled media work should not start.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        release.SetResult();
        Assert.AreEqual(7, await running);
        await Task.WhenAll(media, voice);
        CollectionAssert.AreEqual(new[] { "voice", "media" }, order);
    }
}
