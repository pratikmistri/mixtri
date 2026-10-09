using System.Text.Json;
using System.Text.Json.Serialization;
using Mixtri.Core.Timeline;

namespace Mixtri.Tests;

[TestClass]
public class TextSlidePresetTests
{
    [TestMethod]
    public void PresetsPreserveContentTimingAndIdentity()
    {
        foreach (var preset in TextSlidePresets.All)
        {
            var slide = new TextSlideSegment
            {
                Text = "My mixed Case title", Duration = TimeSpan.FromSeconds(7),
                Start = TimeSpan.FromSeconds(4), TrackIndex = 2,
                Animation = TextSlideAnimation.CascadeFadeUp,
                TextInStart = TimeSpan.FromSeconds(.5), TextInDuration = TimeSpan.FromSeconds(.8),
                TextOutEnd = TimeSpan.FromSeconds(6), TextOutDuration = TimeSpan.FromSeconds(.4),
                InTransition = new TransitionConfig { Type = TransitionType.Wipe },
            };
            var before = slide with { };
            preset.Apply(slide);
            Assert.AreEqual(before.Text, slide.Text);
            Assert.AreEqual(before.Id, slide.Id);
            Assert.AreEqual(before.Start, slide.Start);
            Assert.AreEqual(before.Duration, slide.Duration);
            Assert.AreEqual(before.TrackIndex, slide.TrackIndex);
            Assert.AreEqual(before.Animation, slide.Animation);
            Assert.AreEqual(before.TextInStart, slide.TextInStart);
            Assert.AreEqual(before.TextInDuration, slide.TextInDuration);
            Assert.AreEqual(before.TextOutEnd, slide.TextOutEnd);
            Assert.AreEqual(before.TextOutDuration, slide.TextOutDuration);
            Assert.AreEqual(before.InTransition, slide.InTransition);
            Assert.AreSame(preset, TextSlidePresets.Selected(slide));
            Assert.AreEqual(SlideGradientMotion.SubtleDrift, slide.GradientMotion);
        }
    }

    [TestMethod]
    public void PresetAndCustomUndoRestoreExactAppearanceWithoutRevertingLaterText()
    {
        var slide = new TextSlideSegment
        {
            BackgroundType = SlideBackgroundType.Image, BackgroundImagePath = "original.png",
            FontFamily = "Georgia", IsItalic = true, TextX = .3, TextY = .7,
        };
        var original = TextSlideAppearance.Capture(slide);
        var model = new TimelineModel(); model.Segments.Add(slide);
        var undo = new UndoRedoManager(model);
        undo.Execute(new ApplyTextSlidePresetOperation(slide.Id, TextSlidePresets.All[6]));
        var styled = TextSlideAppearance.Capture(slide);
        undo.Execute(new ApplyTextSlidePresetOperation(slide.Id, null));
        Assert.IsNull(TextSlidePresets.Selected(slide));
        Assert.AreEqual(styled with { PresetId = null }, TextSlideAppearance.Capture(slide));
        slide.Text = "Edited after selecting Custom";
        undo.Undo();
        Assert.AreEqual(styled, TextSlideAppearance.Capture(slide));
        undo.Undo();
        Assert.AreEqual(original, TextSlideAppearance.Capture(slide));
        Assert.AreEqual("Edited after selecting Custom", slide.Text);
        undo.Redo();
        Assert.AreEqual(styled, TextSlideAppearance.Capture(slide));
    }

    [TestMethod]
    public void SamePresetAndMissingSlideDoNotCreateUndoEntries()
    {
        var slide = new TextSlideSegment();
        var model = new TimelineModel(); model.Segments.Add(slide);
        TextSlidePresets.All[0].Apply(slide);
        var undo = new UndoRedoManager(model);
        undo.Execute(new ApplyTextSlidePresetOperation(slide.Id, TextSlidePresets.All[0]));
        undo.Execute(new ApplyTextSlidePresetOperation("missing", TextSlidePresets.All[1]));
        Assert.IsFalse(undo.CanUndo);
    }

    [TestMethod]
    public void SelectionSurvivesTextAndTimingChangesButDetectsCustomAppearance()
    {
        var slide = new TextSlideSegment(); var preset = TextSlidePresets.All.Single(p => p.Id == "cyberpunk"); preset.Apply(slide);
        slide.Text = "New title"; slide.Duration = TimeSpan.FromSeconds(9);
        slide.Animation = TextSlideAnimation.FadeIn;
        Assert.AreSame(preset, TextSlidePresets.Selected(slide));
        Assert.AreEqual("NEW TITLE", slide.DisplayText);
        slide.FontSize += 4;
        Assert.IsNull(TextSlidePresets.Selected(slide));
    }

    [TestMethod]
    public void SavedPresetRoundTripsAndLegacySlidesRemainCustomWithOriginalMotion()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var slide = new TextSlideSegment { Text = "Original Case" };
        TextSlidePresets.All[7].Apply(slide);
        var restored = JsonSerializer.Deserialize<TextSlideSegment>(JsonSerializer.Serialize(slide, options), options)!;
        Assert.AreEqual(TextSlideAppearance.Capture(slide), TextSlideAppearance.Capture(restored));
        Assert.AreEqual("Original Case", restored.Text);
        var legacy = JsonSerializer.Deserialize<TextSlideSegment>("{\"Text\":\"Legacy\",\"BackgroundType\":\"Gradient\"}", options)!;
        Assert.IsNull(TextSlidePresets.Selected(legacy));
        Assert.AreEqual(SlideGradientMotion.OrganicWave, legacy.GradientMotion);
        Assert.AreEqual("Legacy", legacy.DisplayText);
    }

    [TestMethod]
    public void BundledImagesExistAndUntrustedNamesCannotEscapeAssetDirectory()
    {
        var images = TextSlidePresets.All.Where(p => p.ImageName is not null).ToArray();
        Assert.AreEqual(7, images.Length);
        foreach (var preset in images)
        {
            var slide = new TextSlideSegment();
            preset.Apply(slide);
            Assert.AreEqual(SlideBackgroundType.Image, slide.BackgroundType);
            Assert.IsTrue(File.Exists(TitleSlideImages.Resolve(slide.BackgroundImagePath)), preset.Name);
        }
        Assert.IsNull(TitleSlideImages.Resolve(TitleSlideImages.Prefix + "../secret"));
        Assert.AreEqual("custom.png", TitleSlideImages.Resolve("custom.png"));
    }
}
