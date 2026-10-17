namespace Mixtri.Core.Timeline;

/// <summary>Appearance only: applying a chapter preset never changes content or timing.</summary>
public sealed record TextSlidePreset(
    string Id, string Name, string BackgroundColor, string GradientEndColor,
    string TextColor, string FontFamily, double FontSize, bool IsBold,
    double GradientAngle, SlideTextAlignment Alignment, bool Uppercase = false, string? ImageName = null)
{
    public void Apply(TextSlideSegment slide)
    {
        slide.AppearancePresetId = Id;
        slide.FontFamily = FontFamily;
        slide.FontSize = FontSize;
        slide.IsBold = IsBold;
        slide.IsItalic = false;
        slide.Uppercase = Uppercase;
        slide.TextColor = TextColor;
        slide.TextAlignment = Alignment;
        // ComputeTextRect is 84% wide: center .54 puts its left edge at 12%.
        slide.TextX = Alignment == SlideTextAlignment.Left ? .54 : .5;
        slide.TextY = .5;
        slide.BackgroundType = ImageName is null ? SlideBackgroundType.Gradient : SlideBackgroundType.Image;
        slide.BackgroundColor = BackgroundColor;
        slide.GradientEndColor = GradientEndColor;
        slide.GradientAngle = GradientAngle;
        slide.BackgroundImagePath = ImageName is null ? null : TitleSlideImages.Prefix + ImageName;
        slide.GradientMotion = SlideGradientMotion.SubtleDrift;
    }

    public bool Matches(TextSlideSegment slide)
    {
        var expected = slide with { };
        Apply(expected);
        return TextSlideAppearance.Capture(slide) == TextSlideAppearance.Capture(expected);
    }
}

/// <summary>Windows inbox fonts only; sizes are in the existing slide font-size units.</summary>
public static class TextSlidePresets
{
    public static IReadOnlyList<TextSlidePreset> All { get; } = Array.AsReadOnly(new[]
    {
        Image("eclipse", "Eclipse", "#202124", "#F4F2EF"),
        Image("porcelain", "Porcelain", "#F1E8D8", "#302D28"),
        Image("celadon", "Celadon", "#D1DDC8", "#203C30"),
        Image("mist-blue", "Mist Blue", "#D0E5F5", "#223C56"),
        Image("lilac", "Lilac", "#D4C2E8", "#392A50"),
        Image("clay", "Clay", "#F5CEAE", "#522D24"),
        Image("champagne", "Champagne", "#F2E2C8", "#493822"),
        new TextSlidePreset("midnight", "Midnight", "#0C1524", "#263E59", "#F0F3F7", "Bahnschrift", 104, false, 25, SlideTextAlignment.Left),
        new TextSlidePreset("cyberpunk", "Cyberpunk", "#0D102C", "#681C70", "#BAFFF3", "Bahnschrift", 124, true, 25, SlideTextAlignment.Left, true),
        new TextSlidePreset("brutalism", "Brutalism", "#FAFF58", "#CCDF2A", "#15170D", "Impact", 152, false, 90, SlideTextAlignment.Left, true),
        new TextSlidePreset("cobalt", "Cobalt", "#143FFF", "#35218F", "#F5F3FF", "Bahnschrift", 132, true, 35, SlideTextAlignment.Center, true),
        new TextSlidePreset("infrared", "Infrared", "#3B081A", "#A1243F", "#FFEDE1", "Impact", 144, false, 40, SlideTextAlignment.Center, true),
    });

    private static TextSlidePreset Image(string name, string label, string background, string text) =>
        new("image-" + name, label, background, background, text, "Segoe UI", 104, false,
            0, SlideTextAlignment.Left, ImageName: name);

    public static TextSlidePreset? Selected(TextSlideSegment slide) =>
        All.FirstOrDefault(p => p.Id == slide.AppearancePresetId && p.Matches(slide));
}

/// <summary>Exact style snapshot for undo, including the former custom image and placement.</summary>
public sealed record TextSlideAppearance(
    string? PresetId, string FontFamily, double FontSize, bool IsBold, bool IsItalic,
    bool Uppercase, string TextColor, SlideTextAlignment Alignment, double X, double Y,
    SlideBackgroundType BackgroundType, string BackgroundColor, string GradientEndColor,
    double GradientAngle, string? ImagePath, SlideGradientMotion Motion)
{
    public static TextSlideAppearance Capture(TextSlideSegment s) => new(
        s.AppearancePresetId, s.FontFamily, s.FontSize, s.IsBold, s.IsItalic, s.Uppercase,
        s.TextColor, s.TextAlignment, s.TextX, s.TextY, s.BackgroundType,
        s.BackgroundColor, s.GradientEndColor, s.GradientAngle, s.BackgroundImagePath, s.GradientMotion);

    public void Apply(TextSlideSegment s)
    {
        s.AppearancePresetId = PresetId;
        s.FontFamily = FontFamily; s.FontSize = FontSize; s.IsBold = IsBold; s.IsItalic = IsItalic;
        s.Uppercase = Uppercase; s.TextColor = TextColor; s.TextAlignment = Alignment;
        s.TextX = X; s.TextY = Y; s.BackgroundType = BackgroundType;
        s.BackgroundColor = BackgroundColor; s.GradientEndColor = GradientEndColor;
        s.GradientAngle = GradientAngle; s.BackgroundImagePath = ImagePath; s.GradientMotion = Motion;
    }
}

/// <summary>One undo step for a preset or for switching to Custom without changing the look.</summary>
public sealed class ApplyTextSlidePresetOperation(string segmentId, TextSlidePreset? preset) : IEditOperation
{
    private TextSlideAppearance? _before;
    public bool ChangedModel { get; private set; }
    public string Description => preset is null ? "Customize Title Slide" : $"Apply {preset.Name} Title Style";
    public void Execute(TimelineModel model)
    {
        ChangedModel = false;
        var slide = model.Segments.OfType<TextSlideSegment>().FirstOrDefault(s => s.Id == segmentId);
        if (slide is null) return;
        _before = TextSlideAppearance.Capture(slide);
        if (preset is null) slide.AppearancePresetId = null;
        else preset.Apply(slide);
        ChangedModel = _before != TextSlideAppearance.Capture(slide);
    }
    public void Undo(TimelineModel model)
    {
        var slide = model.Segments.OfType<TextSlideSegment>().FirstOrDefault(s => s.Id == segmentId);
        if (slide is not null) _before?.Apply(slide);
    }
}
