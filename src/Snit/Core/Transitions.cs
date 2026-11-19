using System.Globalization;

namespace Snit.Core;

/// <summary>Every transition Snit has (saved by name in project files, so new ones go at the end).</summary>
public enum TransitionKind
{
    CrossDissolve, DipToBlack,
    DipToWhite, FilmDissolve, AdditiveDissolve, NonAdditiveDissolve, DitherDissolve,
    IrisRound, IrisBox, IrisDiamond, IrisCross,
    Wipe, BandWipe, BarnDoors, CheckerWipe, CheckerBoard, ClockWipe, GradientWipe, Inset, PaintSplatter,
    Pinwheel, RadialWipe, RandomBlocks, RandomWipe, SpiralBoxes, VenetianBlinds, WedgeWipe, ZigZagBlocks,
    Slide, Push, CrossZoom, FlipOver,
}

/// <summary>A transition at the start or end of a clip, <see cref="Frames"/> long.</summary>
/// <param name="Direction">For wipes and slides: 0 = from the left, 1 = from the right, 2 = from the top, 3 = from the bottom.</param>
public sealed record Transition(TransitionKind Kind, long Frames, int Direction = 0);

/// <summary>How a transition works.</summary>
public enum TransitionStyle
{
    /// <summary>The new clip fades in over the old one (with a speed curve).</summary>
    Dissolve,
    /// <summary>Through black or white.</summary>
    Dip,
    /// <summary>The new clip appears through a moving shape (wipes, irises).</summary>
    Reveal,
    /// <summary>The new clip slides in over the old one.</summary>
    Slide,
    /// <summary>The new clip slides in and pushes the old one out.</summary>
    Push,
    /// <summary>The old clip zooms in, the new one zooms out.</summary>
    Zoom,
    /// <summary>The old clip turns away, the new one turns towards you.</summary>
    Flip,
}

/// <summary>A transition in the Transitions tab.</summary>
/// <param name="Shape">For Reveal: the wipe shape for a direction (see <see cref="Reveal"/>).</param>
/// <param name="Curve">For Dissolve: how visible the new clip is at progress P (0..1), as a formula.</param>
public sealed record TransitionDefinition(TransitionKind Kind, string Category, string Name, string Description,
    TransitionStyle Style, Func<int, string>? Shape = null, double Feather = 0.03, bool HasDirection = false, string Curve = "P")
{
    public override string ToString() => Name;
}

/// <summary>All transitions, grouped like Premiere Pro's Video Transitions folder.</summary>
public static class TransitionCatalog
{
    // Position along the wipe direction (0 where it starts, 1 where it ends) and across it.
    public static string Along(int direction) => direction switch { 1 => "(1-X/W)", 2 => "(Y/H)", 3 => "(1-Y/H)", _ => "(X/W)" };
    public static string Across(int direction) => direction >= 2 ? "(X/W)" : "(Y/H)";

    private const string Dx = "(X/W-0.5)", Dy = "(Y/H-0.5)";
    private const string ClockAngle = "mod(atan2(X/W-0.5,0.5-Y/H)/(2*PI)+1,1)";

    private const string Dissolve = "Dissolve", Iris = "Iris", Slides = "Slide", Wipes = "Wipe", Zoom = "Zoom", Motion = "3D Motion";

    public static readonly IReadOnlyList<string> Categories = [Dissolve, Iris, Slides, Wipes, Zoom, Motion];

    public static IReadOnlyList<TransitionDefinition> All { get; } =
    [
        // ---------------- Dissolve ----------------
        new(TransitionKind.CrossDissolve, Dissolve, "Cross Dissolve", "The new clip fades in over the old one.", TransitionStyle.Dissolve),
        new(TransitionKind.FilmDissolve, Dissolve, "Film Dissolve", "A softer dissolve: slow at the start and the end.",
            TransitionStyle.Dissolve, Curve: "P*P*(3-2*P)"),
        new(TransitionKind.AdditiveDissolve, Dissolve, "Additive Dissolve", "A dissolve where the new clip shows up quickly.",
            TransitionStyle.Dissolve, Curve: "sqrt(P)"),
        new(TransitionKind.NonAdditiveDissolve, Dissolve, "Non-Additive Dissolve", "A dissolve where the new clip comes in late.",
            TransitionStyle.Dissolve, Curve: "P*P*P"),
        new(TransitionKind.DitherDissolve, Dissolve, "Dither Dissolve", "The new clip appears dot by dot.",
            TransitionStyle.Reveal, _ => Reveal.Random("floor(X)", "floor(Y)"), Feather: 0.002),
        new(TransitionKind.DipToBlack, Dissolve, "Dip to Black", "Fades to black, then into the new clip.", TransitionStyle.Dip),
        new(TransitionKind.DipToWhite, Dissolve, "Dip to White", "Fades to white, then into the new clip.", TransitionStyle.Dip),

        // ---------------- Iris ----------------
        new(TransitionKind.IrisRound, Iris, "Iris Round", "A growing circle shows the new clip.",
            TransitionStyle.Reveal, _ => $"hypot({Dx},{Dy}*H/W)/hypot(0.5,0.5*H/W)"),
        new(TransitionKind.IrisBox, Iris, "Iris Box", "A growing rectangle shows the new clip.",
            TransitionStyle.Reveal, _ => $"max(abs({Dx}),abs({Dy}))*2"),
        new(TransitionKind.IrisDiamond, Iris, "Iris Diamond", "A growing diamond shows the new clip.",
            TransitionStyle.Reveal, _ => $"abs({Dx})+abs({Dy})"),
        new(TransitionKind.IrisCross, Iris, "Iris Cross", "A growing cross shows the new clip.",
            TransitionStyle.Reveal, _ => $"min(abs({Dx}),abs({Dy}))*2"),

        // ---------------- Slide ----------------
        new(TransitionKind.Slide, Slides, "Slide", "The new clip slides in over the old one.", TransitionStyle.Slide, HasDirection: true),
        new(TransitionKind.Push, Slides, "Push", "The new clip pushes the old one out.", TransitionStyle.Push, HasDirection: true),

        // ---------------- Wipe ----------------
        new(TransitionKind.Wipe, Wipes, "Wipe", "A straight edge moves across.",
            TransitionStyle.Reveal, d => Along(d), HasDirection: true),
        new(TransitionKind.BandWipe, Wipes, "Band Wipe", "Stripes wipe in from both sides.",
            TransitionStyle.Reveal, d => $"if(mod(floor({Across(d)}*6),2),1-{Along(d)},{Along(d)})", HasDirection: true),
        new(TransitionKind.BarnDoors, Wipes, "Barn Doors", "Opens from the middle like two doors.",
            TransitionStyle.Reveal, d => d >= 2 ? $"abs({Dy})*2" : $"abs({Dx})*2", HasDirection: true),
        new(TransitionKind.CheckerWipe, Wipes, "Checker Wipe", "A checkerboard of squares wipes across.",
            TransitionStyle.Reveal, d => $"mod(floor(X/W*8)+floor(Y/H*6),2)*0.5+mod({Along(d)}*8,1)*0.5", HasDirection: true),
        new(TransitionKind.CheckerBoard, Wipes, "CheckerBoard", "Half the squares, then the other half.",
            TransitionStyle.Reveal, _ => "if(mod(floor(X/W*8)+floor(Y/H*6),2),0.7,0.3)", Feather: 0.2),
        new(TransitionKind.ClockWipe, Wipes, "Clock Wipe", "Sweeps round like a clock hand.",
            TransitionStyle.Reveal, _ => ClockAngle, Feather: 0.01),
        new(TransitionKind.GradientWipe, Wipes, "Gradient Wipe", "A very soft diagonal wipe.",
            TransitionStyle.Reveal, d => d % 2 == 0 ? "(X/W+Y/H)/2" : "(2-X/W-Y/H)/2", Feather: 0.35, HasDirection: true),
        new(TransitionKind.Inset, Wipes, "Inset", "Grows from a corner.",
            TransitionStyle.Reveal, d => d switch { 1 => "max(1-X/W,Y/H)", 2 => "max(X/W,1-Y/H)", 3 => "max(1-X/W,1-Y/H)", _ => "max(X/W,Y/H)" },
            HasDirection: true),
        new(TransitionKind.PaintSplatter, Wipes, "Paint Splatter", "Splashes of the new clip spread out.",
            TransitionStyle.Reveal, _ => "clip(0.5+0.5*sin(X/W*13+sin(Y/H*7)*2)*cos(Y/H*11+sin(X/W*5)*2),0,1)", Feather: 0.08),
        new(TransitionKind.Pinwheel, Wipes, "Pinwheel", "Eight blades turn round.",
            TransitionStyle.Reveal, _ => $"mod({ClockAngle}*8,1)", Feather: 0.02),
        new(TransitionKind.RadialWipe, Wipes, "Radial Wipe", "Sweeps round from a corner.",
            TransitionStyle.Reveal, _ => "atan2(Y/H,X/W)/(PI/2)", Feather: 0.02),
        new(TransitionKind.RandomBlocks, Wipes, "Random Blocks", "Blocks of the new clip appear at random.",
            TransitionStyle.Reveal, _ => Reveal.Blocks("W/12", "H/8"), Feather: 0.002),
        new(TransitionKind.RandomWipe, Wipes, "Random Wipe", "Blocks wipe across in a ragged line.",
            TransitionStyle.Reveal, d => $"{Along(d)}*0.7+{Reveal.Random("floor(X/W*16)", "floor(Y/H*9)")}*0.3", Feather: 0.002, HasDirection: true),
        new(TransitionKind.SpiralBoxes, Wipes, "Spiral Boxes", "Boxes spiral in from the edge to the middle.",
            TransitionStyle.Reveal, _ => $"(floor((1-max(abs({Dx}),abs({Dy}))*2)*4)+{ClockAngle})/4.0001", Feather: 0.005),
        new(TransitionKind.VenetianBlinds, Wipes, "Venetian Blinds", "Stripes open like blinds.",
            TransitionStyle.Reveal, d => d >= 2 ? Reveal.Blinds(10, upright: false) : Reveal.Blinds(10, upright: true), HasDirection: true),
        new(TransitionKind.WedgeWipe, Wipes, "Wedge Wipe", "Opens like a fan from the top middle.",
            TransitionStyle.Reveal, _ => "abs(atan2(X/W-0.5,0.5-Y/H))/PI", Feather: 0.01),
        new(TransitionKind.ZigZagBlocks, Wipes, "Zig-Zag Blocks", "Rows of blocks, back and forth.",
            TransitionStyle.Reveal, _ => "(floor(Y/H*6)+if(mod(floor(Y/H*6),2),1-floor(X/W*10)/10,floor(X/W*10)/10))/6", Feather: 0.002),

        // ---------------- Zoom ----------------
        new(TransitionKind.CrossZoom, Zoom, "Cross Zoom", "Zooms into the old clip and out of the new one.", TransitionStyle.Zoom),

        // ---------------- 3D Motion ----------------
        new(TransitionKind.FlipOver, Motion, "Flip Over", "The picture turns over like a card.", TransitionStyle.Flip),
    ];

    public static TransitionDefinition Find(TransitionKind kind) => All.First(t => t.Kind == kind);

    /// <summary>The direction names for the inspector.</summary>
    public static readonly IReadOnlyList<string> Directions = ["From the left", "From the right", "From the top", "From the bottom"];

    /// <summary>Which way the new clip moves in (as a fraction of the picture size).</summary>
    public static (int X, int Y) Vector(int direction) => direction switch { 1 => (1, 0), 2 => (0, -1), 3 => (0, 1), _ => (-1, 0) };
}

/// <summary>
/// A clip as it is really shown and heard once transitions are taken into account: maybe a bit
/// longer (a cross-dissolve uses footage just beyond the cut), with fades. <see cref="Clip"/> is
/// an adjusted copy; the original on the timeline is left alone.
/// </summary>
/// <param name="Enter">A transition that brings this clip in (other than a plain dissolve or dip), over its first <paramref name="EnterFrames"/>.</param>
/// <param name="Leave">A transition that takes this clip out, over its last <paramref name="LeaveFrames"/>.</param>
public sealed record RenderClip(
    Clip Clip, Clip Original,
    long FadeInFrames, long FadeOutFrames,   // from see-through (or silent) to visible, and back
    long BlackInFrames, long BlackOutFrames, // from black (or white) to visible, and back
    int Slot,                                // 0 or 1, alternating per clip on a track
    Transition? Enter = null, long EnterFrames = 0, Transition? Leave = null, long LeaveFrames = 0,
    bool WhiteIn = false, bool WhiteOut = false)
{
    /// <summary>How visible the clip is at a timeline frame: 0 = see-through, 1 = solid.</summary>
    public double VisibilityAt(long frame) => Ramp(frame, FadeInFrames, FadeOutFrames);

    /// <summary>How bright the clip is at a timeline frame: 0 = black (or white), 1 = normal.</summary>
    public double BrightnessAt(long frame) => Ramp(frame, BlackInFrames, BlackOutFrames);

    private double Ramp(long frame, long rise, long fall)
    {
        double value = 1;
        if (rise > 0) value = Math.Min(value, Math.Clamp((frame - Clip.Start) / (double)rise, 0, 1));
        if (fall > 0) value = Math.Min(value, Math.Clamp((Clip.End - frame) / (double)fall, 0, 1));
        return value;
    }
}

/// <summary>
/// What transitions do to a clip at one frame: how see-through it is, a wipe mask, how far it
/// is moved, zoomed or squeezed. Worked out from formulas (see <see cref="TransitionFormulas"/>)
/// that the export gives to FFmpeg as they are, so preview and export match.
/// </summary>
public sealed record TransitionState(double Opacity = 1, double ShiftX = 0, double ShiftY = 0, double Zoom = 1, double Squeeze = 1,
    string? Mask = null, double MaskP = 0, double MaskQ = 0)
{
    public static readonly TransitionState None = new();
    public bool IsNone => this == None;
}

/// <summary>
/// The formulas behind each transition style. P is how far the transition bringing the clip in
/// is (0..1, over the clip's first frames) and Q how far the one taking it out is (over its last
/// frames). Shifts are fractions of the picture size; they are multiplied by the direction.
/// </summary>
public static class TransitionFormulas
{
    public sealed record Parts(string Opacity = "1", string Shift = "0", string Zoom = "1", string Squeeze = "1", string? Mask = null);

    /// <summary>The formulas for a clip coming in (variable P).</summary>
    public static Parts In(Transition t)
    {
        TransitionDefinition d = TransitionCatalog.Find(t.Kind);
        return d.Style switch
        {
            TransitionStyle.Dissolve => new(Opacity: d.Curve),
            TransitionStyle.Reveal => new(Mask: Reveal.Appeared(d.Shape!(t.Direction), "P", d.Feather)),
            TransitionStyle.Slide or TransitionStyle.Push => new(Shift: "(1-P)"),
            TransitionStyle.Zoom => new(Opacity: "clip((P-0.45)/0.1,0,1)", Zoom: "(1+3*pow(1-clip(2*P-1,0,1),2))"),
            TransitionStyle.Flip => new(Opacity: "gt(P,0.5)", Squeeze: "clip(2*P-1,0,1)"),
            _ => new(),
        };
    }

    /// <summary>The formulas for a clip going out (variable Q).</summary>
    public static Parts Out(Transition t)
    {
        TransitionDefinition d = TransitionCatalog.Find(t.Kind);
        return d.Style switch
        {
            TransitionStyle.Dissolve => new(Opacity: $"(1-({d.Curve.Replace("P", "Q")}))"),
            TransitionStyle.Reveal => new(Mask: $"(1-{Reveal.Appeared(d.Shape!(t.Direction), "Q", d.Feather)})"),
            // Going out the far side: what came in from the left leaves to the right.
            TransitionStyle.Slide or TransitionStyle.Push => new(Shift: "(-Q)"),
            TransitionStyle.Zoom => new(Opacity: "(1-clip((Q-0.45)/0.1,0,1))", Zoom: "(1+3*pow(clip(2*Q,0,1),2))"),
            TransitionStyle.Flip => new(Opacity: "lt(Q,0.5)", Squeeze: "clip(1-2*Q,0,1)"),
            _ => new(),
        };
    }

    /// <summary>How far along a transition is at a frame (0..1), counting frame middles.</summary>
    public static double Progress(long framesIn, long length) => length <= 0 ? 1 : Math.Clamp((framesIn + 0.5) / length, 0, 1);

    /// <summary>The state of a clip at a timeline frame.</summary>
    public static TransitionState At(Transition? enter, long enterFrames, Transition? leave, long leaveFrames, long start, long end, long frame)
    {
        bool entering = enter is not null && enterFrames > 0 && frame < start + enterFrames;
        bool leaving = leave is not null && leaveFrames > 0 && frame >= end - leaveFrames;
        if (!entering && !leaving) return TransitionState.None;

        double p = entering ? Progress(frame - start, enterFrames) : 1;
        double q = leaving ? Progress(frame - (end - leaveFrames), leaveFrames) : 0;
        double V(string formula) => Formula.Evaluate(formula, p: p, q: q);

        double opacity = 1, sx = 0, sy = 0, zoom = 1, squeeze = 1;
        var masks = new List<string>();
        void Apply(Parts parts, Transition t)
        {
            opacity *= Math.Clamp(V(parts.Opacity), 0, 1);
            var (vx, vy) = TransitionCatalog.Vector(t.Direction);
            double shift = V(parts.Shift);
            sx += shift * vx;
            sy += shift * vy;
            zoom *= V(parts.Zoom);
            squeeze *= Math.Clamp(V(parts.Squeeze), 0, 1);
            if (parts.Mask is string m) masks.Add(m);
        }
        if (entering) Apply(In(enter!), enter!);
        if (leaving) Apply(Out(leave!), leave!);

        return new TransitionState(opacity, sx, sy, zoom, squeeze,
            masks.Count == 0 ? null : string.Join("*", masks.Select(m => $"({m})")), p, q);
    }
}

/// <summary>
/// Works out what transitions do to the clips of one track. The export, the preview picture
/// and the preview sound all use this, so they always agree.
/// </summary>
public static class TransitionPlan
{
    public const long DefaultFrames = 30; // 1 second at 30 fps

    public static List<RenderClip> Expand(Track track, double fps)
    {
        var clips = track.Clips.OrderBy(c => c.Start).ToList();
        int n = clips.Count;
        var copies = clips.Select(c => c.Copy()).ToList();
        var fadeIn = new long[n];
        var fadeOut = new long[n];
        var blackIn = new long[n];
        var blackOut = new long[n];
        var whiteIn = new bool[n];
        var whiteOut = new bool[n];
        var enter = new Transition?[n];
        var enterFrames = new long[n];
        var leave = new Transition?[n];
        var leaveFrames = new long[n];
        bool isAudio = track.Kind == TrackKind.Audio;

        for (int i = 0; i < n; i++)
        {
            Clip c = clips[i];
            int prev = i > 0 && clips[i - 1].End == c.Start ? i - 1 : -1;
            int next = i + 1 < n && clips[i + 1].Start == c.End ? i + 1 : -1;

            if (c.TransitionIn is Transition t)
            {
                long d = Math.Clamp(t.Frames, 1, c.Length);
                TransitionDefinition def = TransitionCatalog.Find(t.Kind);
                if (def.Style != TransitionStyle.Dip)
                {
                    // Centred on the cut: the next clip starts half a transition early and the
                    // previous one keeps going half a transition longer, as far as their files allow.
                    long half = prev < 0 ? 0 : Math.Min(d / 2, Math.Min(
                        TimelineEdits.RoomToGrow(c, fps).BeforeStart,
                        TimelineEdits.RoomToGrow(clips[prev], fps).AfterEnd));
                    long length = half > 0 ? half * 2 : d; // no spare footage: the clip just comes in
                    if (half > 0)
                    {
                        TimelineEdits.MoveEdges(copies[i], -half, 0);
                        TimelineEdits.MoveEdges(copies[prev], 0, half);
                        if (isAudio) fadeOut[prev] = Math.Max(fadeOut[prev], length); // sound: cross-fade
                    }

                    if (isAudio || t.Kind == TransitionKind.CrossDissolve)
                        fadeIn[i] = Math.Max(fadeIn[i], length);
                    else
                    {
                        enter[i] = t;
                        enterFrames[i] = length;
                        // Push, zoom and flip move the old clip too.
                        if (half > 0 && def.Style is TransitionStyle.Push or TransitionStyle.Zoom or TransitionStyle.Flip)
                        {
                            leave[prev] = t;
                            leaveFrames[prev] = length;
                        }
                    }
                }
                else if (prev >= 0)
                {
                    // Dip at a cut: half to black (or white), half back.
                    blackOut[prev] = Math.Max(blackOut[prev], Math.Min(d / 2, clips[prev].Length));
                    blackIn[i] = Math.Max(blackIn[i], Math.Max(1, d / 2));
                    whiteOut[prev] = whiteIn[i] = t.Kind == TransitionKind.DipToWhite && !isAudio;
                }
                else
                {
                    blackIn[i] = Math.Max(blackIn[i], d);
                    whiteIn[i] = t.Kind == TransitionKind.DipToWhite && !isAudio;
                }
            }

            // A transition at the end only counts when the next clip doesn't have one at its start.
            if (c.TransitionOut is Transition o && !(next >= 0 && clips[next].TransitionIn is not null))
            {
                long d = Math.Clamp(o.Frames, 1, c.Length);
                TransitionDefinition def = TransitionCatalog.Find(o.Kind);
                if (def.Style == TransitionStyle.Dip)
                {
                    blackOut[i] = Math.Max(blackOut[i], d);
                    whiteOut[i] = o.Kind == TransitionKind.DipToWhite && !isAudio;
                }
                else if (isAudio || o.Kind == TransitionKind.CrossDissolve) fadeOut[i] = Math.Max(fadeOut[i], d);
                else
                {
                    leave[i] = o;
                    leaveFrames[i] = d;
                }
            }
        }

        return Enumerable.Range(0, n)
            .Select(i => new RenderClip(copies[i], clips[i], fadeIn[i], fadeOut[i], blackIn[i], blackOut[i], i % 2,
                enter[i], enterFrames[i], leave[i], leaveFrames[i], whiteIn[i], whiteOut[i]))
            .ToList();
    }
}
