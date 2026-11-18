using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoiceInput;

namespace VoiceInput.Tests;

[TestClass]
public class InterfaceLanguageTests
{
    [DataTestMethod]
    [DataRow("en", "Start recording")]
    [DataRow("zh-TW", "開始錄音")]
    [DataRow("zh-CN", "开始录音")]
    [DataRow("unsupported", "开始录音")]
    public void RecordingActionUsesSelectedLanguageOrSimplifiedChinese(string language, string expected)
    {
        Assert.AreEqual(expected, InterfaceLanguage.Text(language, "开始录音"));
    }
}
