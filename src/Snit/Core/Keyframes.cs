namespace Snit.Core;

/// <summary>
/// A value at a moment of a clip. <see cref="Frame"/> is counted from the start of the clip,
/// so moving the clip moves its animation with it.
/// </summary>
public sealed record Keyframe(long Frame, double Value);

/// <summary>
/// Animating a clip's transform: for each setting (Scale, Rotation, Opacity, PositionX, PositionY)
/// a list of keyframes. Between two keyframes the value changes evenly; before the first and
/// after the last it stays put.
/// </summary>
public static class Keyframes
{
    /// <summary>The settings that can be animated, by name.</summary>
    public static readonly string[] Animatable = ["Scale", "Rotation", "Opacity", "PositionX", "PositionY"];

    /// <summary>The name for volume keyframes (in dB) on clips on sound tracks.</summary>
    public const string Volume = "Volume";

    /// <summary>The keyframes of one setting, or null when it isn't animated.</summary>
    public static IReadOnlyList<Keyframe>? Of(IReadOnlyDictionary<string, IReadOnlyList<Keyframe>> keyframes, string name)
        => keyframes.TryGetValue(name, out var keys) && keys.Count > 0 ? keys : null;

    /// <summary>Whether a clip's picture is animated (volume keyframes don't count).</summary>
    public static bool AnimatesPicture(IReadOnlyDictionary<string, IReadOnlyList<Keyframe>> keyframes)
        => Animatable.Any(name => keyframes.TryGetValue(name, out var keys) && keys.Count > 0);

    public static IReadOnlyDictionary<string, IReadOnlyList<Keyframe>> None { get; } =
        new Dictionary<string, IReadOnlyList<Keyframe>>();

    /// <summary>The value at a frame (counted from the clip's start).</summary>
    public static double ValueAt(IReadOnlyList<Keyframe> keys, double frame)
    {
        if (keys.Count == 0) return 0;
        if (frame <= keys[0].Frame) return keys[0].Value;
        for (int i = 1; i < keys.Count; i++)
        {
            if (frame <= keys[i].Frame)
            {
                Keyframe a = keys[i - 1], b = keys[i];
                double progress = (frame - a.Frame) / Math.Max(1.0, b.Frame - a.Frame);
                return a.Value + (b.Value - a.Value) * progress;
            }
        }
        return keys[^1].Value;
    }

    /// <summary>The clip's look at a frame (counted from its start): animated settings replaced by their value then.</summary>
    public static ClipLook LookAt(ClipLook look, IReadOnlyDictionary<string, IReadOnlyList<Keyframe>> keyframes, double frame)
    {
        if (keyframes.Count == 0) return look;
        double V(string name, double fallback) =>
            keyframes.TryGetValue(name, out var keys) && keys.Count > 0 ? ValueAt(keys, frame) : fallback;
        return look with
        {
            Scale = V("Scale", look.Scale),
            Rotation = V("Rotation", look.Rotation),
            Opacity = V("Opacity", look.Opacity),
            PositionX = V("PositionX", look.PositionX),
            PositionY = V("PositionY", look.PositionY),
        };
    }

    /// <summary>Adds or replaces a keyframe (returns a new set; the old one is left as it was, for undo).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<Keyframe>> Set(
        IReadOnlyDictionary<string, IReadOnlyList<Keyframe>> keyframes, string name, long frame, double value)
    {
        var result = new Dictionary<string, IReadOnlyList<Keyframe>>(keyframes);
        var list = (keyframes.TryGetValue(name, out var keys) ? keys : []).Where(k => k.Frame != frame).ToList();
        list.Add(new Keyframe(frame, value));
        result[name] = list.OrderBy(k => k.Frame).ToList();
        return result;
    }

    /// <summary>Removes the keyframe at a frame (and the setting's animation when it was the last one).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<Keyframe>> Remove(
        IReadOnlyDictionary<string, IReadOnlyList<Keyframe>> keyframes, string name, long frame)
    {
        var result = new Dictionary<string, IReadOnlyList<Keyframe>>(keyframes);
        if (!keyframes.TryGetValue(name, out var keys)) return result;
        var list = keys.Where(k => k.Frame != frame).ToList();
        if (list.Count == 0) result.Remove(name);
        else result[name] = list;
        return result;
    }

    /// <summary>Moves every keyframe by a number of frames (when the clip's start edge moves).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<Keyframe>> Shift(
        IReadOnlyDictionary<string, IReadOnlyList<Keyframe>> keyframes, long frames)
        => frames == 0 || keyframes.Count == 0 ? keyframes
            : keyframes.ToDictionary(p => p.Key, p => (IReadOnlyList<Keyframe>)p.Value.Select(k => k with { Frame = k.Frame + frames }).ToList());
}
