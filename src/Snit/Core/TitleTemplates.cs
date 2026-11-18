namespace Snit.Core;

/// <summary>A ready-made title look in the Text tab.</summary>
public sealed record TitleTemplate(string Name, TitleStyle Style);

/// <summary>Ready-made titles: drag one onto a video track, then change the text in the inspector.</summary>
public static class TitleTemplates
{
    /// <summary>The see-through dark box used by lower thirds and captions.</summary>
    public const string DarkBox = "#A6000000";

    public static readonly IReadOnlyList<TitleTemplate> All =
    [
        new("Title", new TitleStyle()),
        new("Big title", new TitleStyle { Text = "BIG TITLE", FontSize = 150, FontFamily = "Arial Black" }),
        new("Lower third", new TitleStyle
        {
            Text = "Name Surname", FontSize = 52, X = 0.24, Y = 0.83, Shadow = false, Box = DarkBox,
        }),
        new("Chapter", new TitleStyle { Text = "Chapter one", FontSize = 84, Color = "#FFD54A", FontFamily = "Georgia" }),
        new("Corner tag", new TitleStyle
        {
            Text = "LIVE", FontSize = 40, X = 0.9, Y = 0.08, Shadow = false, Box = "#E6E53935",
        }),
        new("Caption", new TitleStyle
        {
            Text = "Words people say", FontSize = 46, Bold = false, X = 0.5, Y = 0.88, Shadow = false, Box = DarkBox,
        }),
        new("End credits", new TitleStyle { Text = "Thanks for watching", FontSize = 72, Bold = false, Y = 0.45 }),
    ];

    /// <summary>The look of captions (the C1 track): readable white text on a dark box near the bottom.</summary>
    public static TitleStyle CaptionStyle(string text) => new()
    {
        Text = text, FontSize = 46, Bold = false, X = 0.5, Y = 0.88, Shadow = false, Box = DarkBox,
    };
}
