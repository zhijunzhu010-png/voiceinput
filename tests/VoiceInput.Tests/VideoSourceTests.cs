using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VoiceInput.Tests;

[TestClass]
public class VideoSourceTests
{
    [DataTestMethod]
    [DataRow("https://www.youtube.com/watch?v=abc")]
    [DataRow("https://youtu.be/abc")]
    [DataRow("https://www.bilibili.com/video/BV1xx411c7mD")]
    [DataRow("https://b23.tv/abc")]
    public void AcceptsPublicYouTubeAndBilibiliHosts(string value)
    {
        Assert.IsTrue(VideoSource.TryParsePublicUri(value, out var uri));
        Assert.IsNotNull(uri);
    }

    [DataTestMethod]
    [DataRow("https://example.com/video")]
    [DataRow("not-a-url")]
    [DataRow("file:///C:/video.mp4")]
    public void RejectsUnsupportedOrMalformedUrls(string value)
    {
        Assert.IsFalse(VideoSource.TryParsePublicUri(value, out _));
    }
}

[TestClass]
public class VideoTranscriberTests
{
    [TestMethod]
    public void DownloaderArgumentsDisablePlaylistAndRequestAudioProgress()
    {
        var args = VideoTranscriber.BuildDownloadArguments(
            new Uri("https://www.youtube.com/watch?v=abc"), "C:\\temp\\download.%(ext)s");

        CollectionAssert.Contains(args, "--no-playlist");
        CollectionAssert.Contains(args, "--newline");
        CollectionAssert.Contains(args, "-f");
        CollectionAssert.Contains(args, "bestaudio");
        CollectionAssert.Contains(args, "C:\\temp\\download.%(ext)s");
    }

    [TestMethod]
    public void FfmpegArgumentsNormalizeAudioForWhisper()
    {
        var args = VideoTranscriber.BuildFfmpegArguments("input.webm", "output.wav");

        CollectionAssert.Contains(args, "-ac");
        CollectionAssert.Contains(args, "1");
        CollectionAssert.Contains(args, "-ar");
        CollectionAssert.Contains(args, "16000");
        CollectionAssert.Contains(args, "-sample_fmt");
        CollectionAssert.Contains(args, "s16");
        CollectionAssert.Contains(args, "output.wav");
    }
}

[TestClass]
public class DesktopInputTests
{
    [TestMethod]
    public void NormalizeTextReplacesAllLineBreaksWithSpaces()
    {
        Assert.AreEqual("中文 English 混合", DesktopInput.NormalizeText("中文\r\nEnglish\n混合"));
    }
}
