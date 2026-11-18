using System.IO;
using Snit.Core;

namespace Snit.Services;

/// <summary>
/// The FFmpeg recipe for a clip's effects, in order, top to bottom. Both the export and the
/// preview decoder use it, so effects look the same in both. Starts with a comma, or is empty.
/// </summary>
public static class EffectFilters
{
    private static string N(double v) => ProcessRunner.Num(Math.Round(v, 4));

    public static string Chain(IReadOnlyList<ClipEffect> effects)
        => string.Concat(effects.Where(e => e.Enabled).Select(One));

    /// <summary>True when an effect can make parts of the picture see-through (crop, green screen).</summary>
    public static bool MakesTransparency(IReadOnlyList<ClipEffect> effects)
        => effects.Any(e => e.Enabled && EffectCatalog.Find(e.Kind)?.MakesTransparency == true);

    private static string One(ClipEffect e)
    {
        switch (e.Kind)
        {
            case "blur":
                return e["amount"] > 0 ? $",gblur=sigma={N(e["amount"] * 0.3)}" : "";

            case "sharpen":
                return e["amount"] > 0 ? $",unsharp=5:5:{N(e["amount"] / 40)}" : "";

            case "vignette":
                return e["amount"] > 0 ? $",vignette=angle={N(Math.PI / 2 * e["amount"] / 100)}" : "";

            case "flip":
                return (e["horizontal"] >= 0.5 ? ",hflip" : "") + (e["vertical"] >= 0.5 ? ",vflip" : "");

            case "crop":
            {
                double l = e["left"] / 100, r = e["right"] / 100, t = e["top"] / 100, b = e["bottom"] / 100;
                if (l + r + t + b <= 0) return "";
                double kw = Math.Max(0.02, 1 - l - r), kh = Math.Max(0.02, 1 - t - b);
                // Cut the edges off, then put the picture back on a see-through canvas of the
                // original size, so it stays in the same place and lower tracks show at the edges.
                return $",format=rgba,crop=w=iw*{N(kw)}:h=ih*{N(kh)}:x=iw*{N(l)}:y=ih*{N(t)}" +
                       $",pad=w=iw/{N(kw)}:h=ih/{N(kh)}:x=ow*{N(l)}:y=oh*{N(t)}:color=black@0";
            }

            case "chromakey":
            {
                string hex = (e.Text ?? "#00FF00").TrimStart('#');
                if (hex.Length != 6) hex = "00FF00";
                return $",format=rgba,colorkey=color=0x{hex}:similarity={N(Math.Max(0.01, e["similarity"] / 100))}:blend={N(e["blend"] / 100)}";
            }

            case "lut":
            {
                if (string.IsNullOrEmpty(e.Text) || !File.Exists(e.Text)) return "";
                // FFmpeg wants forward slashes and an escaped drive colon: C\:/Looks/film.cube
                string path = e.Text.Replace('\\', '/').Replace(":", "\\:");
                return $",lut3d=file='{path}'";
            }

            default:
                return "";
        }
    }
}
