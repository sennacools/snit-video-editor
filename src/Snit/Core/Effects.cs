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

/// <summary>A setting of an effect: its name, what it says, its range and normal value.</summary>
/// <param name="IsSwitch">An on/off switch (0 or 1) instead of a slider.</param>
/// <param name="Options">A dropdown instead of a slider: the value is the chosen option's number (0, 1, 2…).</param>
public sealed record EffectParameter(string Name, string Label, double Min, double Max, double Default, string Format,
    bool IsSwitch = false, IReadOnlyList<string>? Options = null)
{
    public bool IsChoice => Options is not null;
}

/// <summary>An effect in the Effects tab.</summary>
/// <param name="Category">The group it is listed under (the same groups as Premiere Pro).</param>
/// <param name="TextKind">"lut" (needs a .cube file), "color" (needs a colour) or null.</param>
/// <param name="MakesTransparency">The effect can make parts of the picture see-through (so lower tracks show).</param>
public sealed record EffectDefinition(string Kind, string Category, string Name, string Description, IReadOnlyList<EffectParameter> Parameters,
    string? TextKind = null, string? DefaultText = null, bool MakesTransparency = false)
{
    public double Default(string name) => Parameters.FirstOrDefault(p => p.Name == name)?.Default ?? 0;

    public ClipEffect Create() => new(Guid.NewGuid(), Kind,
        Parameters.ToDictionary(p => p.Name, p => p.Default), DefaultText);
}

/// <summary>All effects Snit has, grouped like Premiere Pro's Video Effects folder.</summary>
public static class EffectCatalog
{
    // Short ways to write a setting.
    private static EffectParameter Slider(string name, string label, double min, double max, double normal, string format = "{0:0}")
        => new(name, label, min, max, normal, format);
    private static EffectParameter Percent(string name, string label, double min, double max, double normal)
        => new(name, label, min, max, normal, "{0:0}%");
    private static EffectParameter Pixels(string name, string label, double min, double max, double normal)
        => new(name, label, min, max, normal, "{0:0} px");
    private static EffectParameter Degrees(string name, string label, double min, double max, double normal)
        => new(name, label, min, max, normal, "{0:0}°");
    private static EffectParameter Switch(string name, string label, bool on)
        => new(name, label, 0, 1, on ? 1 : 0, "{0:0}", IsSwitch: true);
    private static EffectParameter Choice(string name, string label, int normal, params string[] options)
        => new(name, label, 0, options.Length - 1, normal, "{0:0}", Options: options);

    private const string Adjust = "Adjust", Blur = "Blur & Sharpen", Channel = "Channel", Correction = "Color Correction",
        Distort = "Distort", Generate = "Generate", Image = "Image Control", Keying = "Keying", Noise = "Noise & Grain",
        Perspective = "Perspective", Stylize = "Stylize", Time = "Time", Transform = "Transform", Wipes = "Transition";

    /// <summary>The order of the groups in the Effects tab.</summary>
    public static readonly IReadOnlyList<string> Categories =
        [Adjust, Blur, Channel, Correction, Distort, Generate, Image, Keying, Noise, Perspective, Stylize, Time, Transform, Wipes];

    public static IReadOnlyList<EffectDefinition> All { get; } =
    [
        // ---------------- Adjust ----------------
        new("brightness_contrast", Adjust, "Brightness & Contrast", "Lighter or darker, flatter or punchier.",
            [Slider("brightness", "Brightness", -100, 100, 0), Slider("contrast", "Contrast", -100, 100, 0)]),
        new("levels", Adjust, "Levels", "Sets the black point, white point and mid-tones.",
            [Slider("in_black", "Input black", 0, 254, 0), Slider("in_white", "Input white", 1, 255, 255),
             Slider("gamma", "Gamma", 0.1, 3, 1, "{0:0.00}"),
             Slider("out_black", "Output black", 0, 255, 0), Slider("out_white", "Output white", 0, 255, 255)]),
        new("channel_mixer", Adjust, "Channel Mixer", "Builds each colour from a mix of red, green and blue.",
            [Percent("rr", "Red ← red", -200, 200, 100), Percent("rg", "Red ← green", -200, 200, 0), Percent("rb", "Red ← blue", -200, 200, 0),
             Percent("gr", "Green ← red", -200, 200, 0), Percent("gg", "Green ← green", -200, 200, 100), Percent("gb", "Green ← blue", -200, 200, 0),
             Percent("br", "Blue ← red", -200, 200, 0), Percent("bg", "Blue ← green", -200, 200, 0), Percent("bb", "Blue ← blue", -200, 200, 100)]),
        new("proc_amp", Adjust, "ProcAmp", "Brightness, contrast, hue and saturation, like on a video mixer.",
            [Slider("brightness", "Brightness", -100, 100, 0), Slider("contrast", "Contrast", -100, 100, 0),
             Degrees("hue", "Hue", -180, 180, 0), Percent("saturation", "Saturation", 0, 300, 100)]),
        new("auto_contrast", Adjust, "Auto Contrast", "Stretches the brightness to use the whole range, keeping the colours.",
            [Percent("strength", "Strength", 0, 100, 100), Slider("smoothing", "Smoothing (frames)", 0, 60, 10)]),
        new("auto_levels", Adjust, "Auto Levels", "Stretches red, green and blue each to the whole range.",
            [Percent("strength", "Strength", 0, 100, 100), Slider("smoothing", "Smoothing (frames)", 0, 60, 10)]),
        new("auto_color", Adjust, "Auto Color", "Takes away a colour cast (grey stays grey).", []),
        new("extract", Adjust, "Extract", "Black and white: a brightness range becomes white, the rest black.",
            [Slider("black", "Black input", 0, 255, 60), Slider("white", "White input", 0, 255, 200), Slider("softness", "Softness", 1, 100, 20)]),
        new("lighting", Adjust, "Lighting Effects", "A spotlight: light in one place, darker around it.",
            [Percent("x", "Light X", 0, 100, 50), Percent("y", "Light Y", 0, 100, 45), Percent("darkness", "Darkness", 0, 100, 60)]),

        // ---------------- Blur & Sharpen ----------------
        new("blur", Blur, "Fast Blur", "Softens the picture.",
            [Slider("amount", "Amount", 0, 100, 20)]),
        new("gaussian_blur", Blur, "Gaussian Blur", "A smooth, even blur, in one or both directions.",
            [Slider("amount", "Blurriness", 0, 100, 20), Choice("dimension", "Blur", 0, "Both ways", "Left-right", "Up-down")]),
        new("directional_blur", Blur, "Directional Blur", "Smears the picture one way, as if it moves fast.",
            [Slider("length", "Length", 0, 100, 20), Choice("direction", "Direction", 0, "Left-right", "Up-down", "Both")]),
        new("camera_blur", Blur, "Camera Blur", "Out of focus, like a camera lens.",
            [Percent("amount", "Amount", 0, 100, 30)]),
        new("channel_blur", Blur, "Channel Blur", "Blurs red, green and blue separately.",
            [Slider("red", "Red", 0, 100, 0), Slider("green", "Green", 0, 100, 0), Slider("blue", "Blue", 0, 100, 20)]),
        new("unsharp_mask", Blur, "Unsharp Mask", "Sharpens fine detail.",
            [Percent("amount", "Amount", 0, 500, 50), Pixels("radius", "Radius", 1, 10, 3)]),
        new("sharpen", Blur, "Sharpen", "Makes edges crisper.",
            [Slider("amount", "Amount", 0, 100, 40)]),

        // ---------------- Channel ----------------
        new("invert", Channel, "Invert", "Turns the picture into its negative.",
            [Choice("channel", "Channel", 0, "All colours", "Red", "Green", "Blue")]),

        // ---------------- Color Correction ----------------
        new("tint", Correction, "Tint", "Turns the picture into shades of one colour.",
            [Percent("amount", "Amount", 0, 100, 100)], TextKind: "color", DefaultText: "#E0B070"),
        new("leave_color", Correction, "Leave Color", "Keeps one colour and makes everything else grey (also called Color Pass).",
            [Slider("similarity", "Range", 1, 100, 30), Slider("blend", "Softness", 0, 100, 20)], TextKind: "color", DefaultText: "#E53935"),
        new("change_color", Correction, "Change Color", "Shifts one group of colours to another hue.",
            [Choice("colors", "Colours", 0, "Reds", "Yellows", "Greens", "Cyans", "Blues", "Magentas"),
             Degrees("hue", "Hue shift", -180, 180, 60), Percent("saturation", "Saturation", -100, 100, 0)]),
        new("color_balance_hls", Correction, "Color Balance (HLS)", "Hue, lightness and saturation.",
            [Degrees("hue", "Hue", -180, 180, 0), Slider("lightness", "Lightness", -100, 100, 0), Slider("saturation", "Saturation", -100, 100, 0)]),
        new("color_balance", Correction, "Color Balance (RGB)", "More or less red, green and blue.",
            [Percent("red", "Red", 0, 200, 100), Percent("green", "Green", 0, 200, 100), Percent("blue", "Blue", 0, 200, 100)]),
        new("video_limiter", Correction, "Video Limiter", "Keeps the colours inside the broadcast-safe range.",
            [Slider("min", "Lowest", 0, 64, 16), Slider("max", "Highest", 191, 255, 235)]),
        new("lut", Correction, "LUT (colour look)", "Applies a colour look from a .cube file.", [], TextKind: "lut"),

        // ---------------- Distort ----------------
        new("mirror", Distort, "Mirror", "Reflects one half of the picture onto the other.",
            [Choice("side", "Reflect", 0, "Left onto right", "Right onto left", "Top onto bottom", "Bottom onto top")]),
        new("lens_distortion", Distort, "Lens Distortion", "Bends the picture like a wide-angle or fisheye lens.",
            [Slider("curvature", "Curvature", -100, 100, -40)]),
        new("spherize", Distort, "Spherize", "Bulges the middle of the picture, as if on a ball.",
            [Slider("amount", "Amount", 0, 100, 50)]),
        new("corner_pin", Distort, "Corner Pin", "Moves each corner, to fit the picture onto a sign or screen.",
            [Percent("tlx", "Top left X", -50, 50, 0), Percent("tly", "Top left Y", -50, 50, 0),
             Percent("trx", "Top right X", -50, 50, 0), Percent("try", "Top right Y", -50, 50, 0),
             Percent("blx", "Bottom left X", -50, 50, 0), Percent("bly", "Bottom left Y", -50, 50, 0),
             Percent("brx", "Bottom right X", -50, 50, 0), Percent("bry", "Bottom right Y", -50, 50, 0)], MakesTransparency: true),
        new("wave_warp", Distort, "Wave Warp", "Moving waves through the picture.",
            [Pixels("height", "Wave height", 0, 100, 10), Pixels("width", "Wave width", 10, 1000, 200), Slider("speed", "Speed", 0, 10, 1, "{0:0.0}")]),
        new("twirl", Distort, "Twirl", "Swirls the middle of the picture.",
            [Degrees("angle", "Angle", -720, 720, 180), Percent("radius", "Radius", 1, 100, 60)]),
        new("offset", Distort, "Offset", "Shifts the picture; what goes off one side comes back on the other.",
            [Percent("x", "Shift X", -100, 100, 25), Percent("y", "Shift Y", -100, 100, 0)]),
        new("magnify", Distort, "Magnify", "A round magnifying glass over part of the picture.",
            [Percent("x", "Centre X", 0, 100, 50), Percent("y", "Centre Y", 0, 100, 50), Percent("size", "Size", 5, 100, 30),
             Percent("zoom", "Magnification", 100, 600, 200)]),
        new("turbulent_displace", Distort, "Turbulent Displace", "Wobbles the picture like heat haze or water.",
            [Pixels("amount", "Amount", 0, 100, 15), Pixels("size", "Size", 5, 500, 80), Slider("speed", "Speed", 0, 10, 1, "{0:0.0}")]),
        new("warp_stabilizer", Distort, "Warp Stabilizer", "Calms down shaky handheld footage.",
            [Pixels("range", "Search range", 8, 64, 32)]),

        // ---------------- Generate ----------------
        new("grid", Generate, "Grid", "Draws a grid over the picture.",
            [Pixels("size", "Cell size", 10, 500, 100), Pixels("line", "Line width", 1, 20, 2), Percent("opacity", "Opacity", 0, 100, 60)],
            TextKind: "color", DefaultText: "#FFFFFF"),
        new("checkerboard", Generate, "Checkerboard", "A checkerboard over the picture.",
            [Pixels("size", "Square size", 10, 500, 80), Percent("opacity", "Opacity", 0, 100, 50)], TextKind: "color", DefaultText: "#000000"),
        new("ramp", Generate, "Ramp", "A gradient from a colour to nothing.",
            [Choice("direction", "From", 0, "Top", "Bottom", "Left", "Right"), Percent("opacity", "Opacity", 0, 100, 70)],
            TextKind: "color", DefaultText: "#1E3A8A"),
        new("circle", Generate, "Circle", "A filled circle.",
            [Percent("x", "Centre X", 0, 100, 50), Percent("y", "Centre Y", 0, 100, 50), Percent("radius", "Radius", 1, 100, 20),
             Pixels("feather", "Soft edge", 1, 100, 2), Percent("opacity", "Opacity", 0, 100, 100)], TextKind: "color", DefaultText: "#FFFFFF"),
        new("lens_flare", Generate, "Lens Flare", "A bright, warm glow, like the sun in the lens.",
            [Percent("x", "Centre X", 0, 100, 25), Percent("y", "Centre Y", 0, 100, 25), Percent("brightness", "Brightness", 0, 300, 100)]),

        // ---------------- Image Control ----------------
        new("black_white", Image, "Black & White", "Takes all colour away.", []),
        new("gamma", Image, "Gamma Correction", "Lighter or darker mid-tones, leaving black and white alone.",
            [Slider("gamma", "Gamma", 0.1, 3, 1, "{0:0.00}")]),

        // ---------------- Keying ----------------
        new("ultrakey", Keying, "Ultra Key", "Green or blue screen, with the green or blue fringe removed.",
            [Slider("similarity", "Range", 1, 100, 15), Slider("blend", "Softness", 0, 100, 10), Switch("despill", "Remove spill", true)],
            TextKind: "color", DefaultText: "#00FF00", MakesTransparency: true),
        new("chromakey", Keying, "Color Key", "Makes one colour see-through.",
            [Slider("similarity", "Range", 1, 100, 30), Slider("blend", "Softness", 0, 100, 10)],
            TextKind: "color", DefaultText: "#00FF00", MakesTransparency: true),
        new("luma_key", Keying, "Luma Key", "Makes dark (or bright) parts see-through.",
            [Choice("key", "Key out", 0, "Dark parts", "Bright parts"), Percent("tolerance", "Range", 0, 100, 10), Percent("softness", "Softness", 0, 100, 10)],
            MakesTransparency: true),
        new("alpha_adjust", Keying, "Alpha Adjust", "Changes or flips the see-through parts.",
            [Percent("opacity", "Opacity", 0, 100, 100), Switch("invert", "Invert", false)], MakesTransparency: true),

        // ---------------- Noise & Grain ----------------
        new("noise", Noise, "Noise", "Film grain.",
            [Slider("amount", "Amount", 0, 100, 15), Switch("color", "Colour noise", false)]),
        new("median", Noise, "Median", "Smooths away small specks (gets a painted look when strong).",
            [Pixels("radius", "Radius", 1, 20, 2)]),
        new("dust_scratches", Noise, "Dust & Scratches", "Removes small dust and scratches, keeping edges.",
            [Pixels("radius", "Radius", 1, 10, 2)]),

        // ---------------- Perspective ----------------
        new("drop_shadow", Perspective, "Drop Shadow", "A soft shadow behind see-through things like titles.",
            [Percent("opacity", "Opacity", 0, 100, 60), Degrees("direction", "Direction", 0, 360, 135),
             Pixels("distance", "Distance", 0, 100, 10), Pixels("softness", "Softness", 0, 60, 10)]),
        new("basic_3d", Perspective, "Basic 3D", "Turns the picture in space (swivel left-right, tilt up-down).",
            [Degrees("swivel", "Swivel", -80, 80, 30), Degrees("tilt", "Tilt", -80, 80, 0), Percent("distance", "Distance", 50, 300, 100)],
            MakesTransparency: true),

        // ---------------- Stylize ----------------
        new("vignette", Stylize, "Vignette", "Darkens the corners.",
            [Slider("amount", "Amount", 0, 100, 50)]),
        new("emboss", Stylize, "Emboss", "A grey relief, as if pressed into metal.",
            [Percent("strength", "Strength", 0, 300, 100)]),
        new("color_emboss", Stylize, "Color Emboss", "Raised edges, keeping the colours.",
            [Percent("strength", "Strength", 0, 300, 100)]),
        new("find_edges", Stylize, "Find Edges", "Shows only the outlines, like a drawing.",
            [Switch("invert", "Dark lines on white", true)]),
        new("mosaic", Stylize, "Mosaic", "Big square pixels (also for blurring faces).",
            [Pixels("block", "Block size", 2, 200, 30)]),
        new("posterize", Stylize, "Posterize", "Fewer colours, like a poster.",
            [Slider("levels", "Levels", 2, 32, 5)]),
        new("solarize", Stylize, "Solarize", "Bright parts turn into their negative.",
            [Slider("threshold", "Threshold", 0, 255, 128)]),
        new("threshold", Stylize, "Threshold", "Pure black and white.",
            [Slider("level", "Level", 0, 255, 128)]),
        new("strobe", Stylize, "Strobe Light", "Flashes a colour on and off.",
            [Slider("period", "Every (seconds)", 0.1, 5, 1, "{0:0.0} s"), Slider("duration", "Flash (seconds)", 0.02, 1, 0.1, "{0:0.00} s"),
             Percent("opacity", "Opacity", 0, 100, 100)], TextKind: "color", DefaultText: "#FFFFFF"),
        new("replicate", Stylize, "Replicate", "Repeats the picture in a grid.",
            [Slider("count", "Copies per side", 2, 4, 2)]),

        // ---------------- Time ----------------
        new("echo", Time, "Echo", "Trails of the frames before, for a dreamy motion look.",
            [Slider("echoes", "Echoes", 1, 8, 3), Percent("decay", "Fade per echo", 10, 100, 60)]),
        new("posterize_time", Time, "Posterize Time", "A choppy, lower frame rate look.",
            [Slider("rate", "Frame rate", 1, 30, 8, "{0:0} fps")]),

        // ---------------- Transform ----------------
        new("crop", Transform, "Crop", "Cuts away the edges (they become see-through).",
            [Percent("left", "Left", 0, 49, 0), Percent("right", "Right", 0, 49, 0), Percent("top", "Top", 0, 49, 0), Percent("bottom", "Bottom", 0, 49, 0)],
            MakesTransparency: true),
        new("flip", Transform, "Flip", "Mirrors the picture.",
            [Switch("horizontal", "Left-right", true), Switch("vertical", "Up-down", false)]),
        new("hflip", Transform, "Horizontal Flip", "Mirrors the picture left to right.", []),
        new("vflip", Transform, "Vertical Flip", "Turns the picture upside down.", []),
        new("edge_feather", Transform, "Edge Feather", "Soft, see-through edges.",
            [Pixels("amount", "Amount", 1, 300, 40)], MakesTransparency: true),

        // ---------------- Transition (as effects: animate nothing, set how far along) ----------------
        new("linear_wipe", Wipes, "Linear Wipe", "Wipes the clip away in a straight line (lower tracks show).",
            [Percent("completion", "Completion", 0, 100, 50), Degrees("angle", "Angle", 0, 360, 0), Pixels("feather", "Soft edge", 0, 300, 20)],
            MakesTransparency: true),
        new("radial_wipe", Wipes, "Radial Wipe", "Wipes the clip away like a clock hand.",
            [Percent("completion", "Completion", 0, 100, 50), Degrees("start", "Start angle", 0, 360, 0)], MakesTransparency: true),
        new("venetian_blinds", Wipes, "Venetian Blinds", "Wipes the clip away in stripes.",
            [Percent("completion", "Completion", 0, 100, 50), Slider("bands", "Stripes", 2, 40, 10), Choice("direction", "Stripes", 0, "Across", "Upright")],
            MakesTransparency: true),
        new("block_dissolve", Wipes, "Block Dissolve", "The clip disappears in random blocks.",
            [Percent("completion", "Completion", 0, 100, 50), Pixels("block", "Block size", 2, 200, 20)], MakesTransparency: true),
    ];

    public static EffectDefinition? Find(string kind) => All.FirstOrDefault(e => e.Kind == kind);
}
