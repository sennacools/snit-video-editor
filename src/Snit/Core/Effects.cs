namespace Snit.Core;

/// <summary>
/// One effect on a clip, for example a blur. <see cref="Kind"/> says which effect (see
/// <see cref="EffectCatalog"/>), <see cref="Values"/> holds its slider values by name, and
/// <see cref="Text"/> holds a file or colour when the effect needs one (LUT file, key colour).
/// A record: to change it, make a new one with <c>with</c>.
/// </summary>
public sealed record ClipEffect(Guid Id, string Kind, IReadOnlyDictionary<string, double> Values, string? Text = null, bool Enabled = true)
{
    public double this[string name] => Values.TryGetValue(name, out double v) ? v : EffectCatalog.Find(Kind)?.Default(name) ?? 0;

    public ClipEffect With(string name, double value)
    {
        var values = new Dictionary<string, double>(Values) { [name] = value };
        return this with { Values = values };
    }
}

/// <summary>A slider of an effect: its name, what it says, its range and normal value.</summary>
/// <param name="IsSwitch">An on/off switch (0 or 1) instead of a slider.</param>
public sealed record EffectParameter(string Name, string Label, double Min, double Max, double Default, string Format, bool IsSwitch = false);

/// <summary>An effect in the Effects tab.</summary>
/// <param name="TextKind">"lut" (needs a .cube file), "color" (needs a colour) or null.</param>
public sealed record EffectDefinition(string Kind, string Name, string Description, IReadOnlyList<EffectParameter> Parameters,
    string? TextKind = null, string? DefaultText = null)
{
    public double Default(string name) => Parameters.FirstOrDefault(p => p.Name == name)?.Default ?? 0;

    public ClipEffect Create() => new(Guid.NewGuid(), Kind,
        Parameters.ToDictionary(p => p.Name, p => p.Default), DefaultText);

    /// <summary>Effects that can make parts of the picture see-through (so lower tracks show).</summary>
    public bool MakesTransparency => Kind is "crop" or "chromakey";
}

/// <summary>All effects Snit has.</summary>
public static class EffectCatalog
{
    public static IReadOnlyList<EffectDefinition> All { get; } =
    [
        new("blur", "Blur", "Softens the picture.",
            [new("amount", "Amount", 0, 100, 20, "{0:0}")]),
        new("sharpen", "Sharpen", "Makes edges crisper.",
            [new("amount", "Amount", 0, 100, 40, "{0:0}")]),
        new("crop", "Crop", "Cuts away the edges (they become see-through).",
            [new("left", "Left", 0, 49, 0, "{0:0}%"), new("right", "Right", 0, 49, 0, "{0:0}%"),
             new("top", "Top", 0, 49, 0, "{0:0}%"), new("bottom", "Bottom", 0, 49, 0, "{0:0}%")]),
        new("vignette", "Vignette", "Darkens the corners.",
            [new("amount", "Amount", 0, 100, 50, "{0:0}")]),
        new("flip", "Flip", "Mirrors the picture.",
            [new("horizontal", "Left-right", 0, 1, 1, "{0:0}", IsSwitch: true), new("vertical", "Up-down", 0, 1, 0, "{0:0}", IsSwitch: true)]),
        new("chromakey", "Green screen", "Makes one colour see-through (green or blue screen).",
            [new("similarity", "Range", 1, 100, 30, "{0:0}"), new("blend", "Softness", 0, 100, 10, "{0:0}")],
            TextKind: "color", DefaultText: "#00FF00"),
        new("lut", "LUT (colour look)", "Applies a colour look from a .cube file.", [], TextKind: "lut"),
    ];

    public static EffectDefinition? Find(string kind) => All.FirstOrDefault(e => e.Kind == kind);
}
