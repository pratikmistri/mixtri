using Mixtri.Core.Media;

namespace Mixtri.Tests;

[TestClass]
public sealed class AudioImportCodecHintTests
{
    [TestMethod]
    public void Ogg_GetsWebMediaExtensionsGuidance()
    {
        StringAssert.Contains(AudioImportService.CodecHint(".ogg"), "Web Media Extensions");
        StringAssert.Contains(AudioImportService.CodecHint(".OGG"), "Web Media Extensions");
    }

    [TestMethod]
    public void NativelySupportedFormats_GetNoHint()
    {
        foreach (var ext in new[] { ".mp3", ".m4a", ".wav", ".flac", null })
            Assert.AreEqual(string.Empty, AudioImportService.CodecHint(ext));
    }
}
