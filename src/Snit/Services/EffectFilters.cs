using System.Globalization;
using System.IO;
using Snit.Core;

namespace Snit.Services;

/// <summary>
/// What an effect needs to know about where it is used.
/// </summary>
/// <param name="PixelScale">
/// Sizes in effects (blur radius, shadow distance…) are measured in a 1080-line picture, so they
/// look the same in a small preview and a 4K export. This is the picture's height / 1080.
/// </param>
/// <param name="Fps">The frame rate of the picture.</param>
/// <param name="Label">A name that is unique in the whole FFmpeg recipe (effects that split the
/// picture into parts need names for the parts).</param>
public sealed record EffectContext(double PixelScale = 1, double Fps = 30, string Label = "fx");

/// <summary>
/// The FFmpeg recipe for a clip's effects, in order, top to bottom. Both the export and the
/// preview decoder use it, so effects look the same in both. Starts with a comma, or is empty.
/// </summary>
public static class EffectFilters
{
    private static string N(double v) => ProcessRunner.Num(Math.Round(v, 4));

    public static string Chain(IReadOnlyList<ClipEffect> effects, EffectContext? context = null)
    {
        context ??= new EffectContext();
        var parts = new List<string>();
        int i = 0;
        foreach (ClipEffect e in effects.Where(e => e.Enabled))
        {
            string one = One(e, context with { Label = $"{context.Label}{i++}" });
            if (one.Length > 0) parts.Add(one);
        }
        return parts.Count == 0 ? "" : "," + string.Join(",", parts);
    }

    /// <summary>True when an effect can make parts of the picture see-through (crop, green screen…).</summary>
    public static bool MakesTransparency(IReadOnlyList<ClipEffect> effects)
        => effects.Any(e => e.Enabled && EffectCatalog.Find(e.Kind)?.MakesTransparency == true);

    // ---- Helpers ----

    /// <summary>"#RRGGBB" → (r, g, b) from 0 to 255; anything else gives the fallback.</summary>
    private static (int R, int G, int B) Rgb(string? text, string fallback)
    {
        string hex = (text ?? fallback).TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            hex = fallback.TrimStart('#');
        v = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((v >> 16) & 255, (v >> 8) & 255, v & 255);
    }

    private static string Hex(string? text, string fallback)
    {
        var (r, g, b) = Rgb(text, fallback);
        return $"0x{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>
    /// Moves pixels around: every output pixel takes the colour from (sx, sy). <paramref name="prelude"/>
    /// can store helper values with st(n, …) and read them back with ld(n).
    /// </summary>
    private static string Remap(string sx, string sy, string prelude = "")
    {
        string P(string plane) => $"'{prelude}{plane}({sx},{sy})'";
        return $"format=gbrap,geq=r={P("r")}:g={P("g")}:b={P("b")}:a={P("alpha")}";
    }

    /// <summary>Mixes a colour over the picture, <paramref name="amount"/> (an expression 0..1) per pixel.</summary>
    private static string Paint((int R, int G, int B) c, string amount, string prelude = "")
    {
        string P(string plane, int value) => $"'{prelude}{plane}(X,Y)*(1-({amount}))+{value}*({amount})'";
        return $"format=gbrap,geq=r={P("r", c.R)}:g={P("g", c.G)}:b={P("b", c.B)}:a='alpha(X,Y)'";
    }

    /// <summary>Changes only how see-through each pixel is (multiplies by <paramref name="amount"/>, 0..1).</summary>
    private static string Alpha(string amount)
        => $"format=gbrap,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*({amount})'";

    /// <summary>A 3×3 convolution for red, green and blue (alpha untouched).</summary>
    private static string Convolve(double[] kernel, double bias)
    {
        string m = string.Join(" ", kernel.Select(N));
        string b = N(bias);
        return $"format=gbrap,convolution='{m}':'{m}':'{m}':'0 0 0 0 1 0 0 0 0':1:1:1:1:{b}:{b}:{b}:0";
    }

    private static string One(ClipEffect e, EffectContext ctx)
    {
        double S = ctx.PixelScale;
        double Px(string name) => e[name] * S;
        string L = ctx.Label;

        switch (e.Kind)
        {
            // ---------------- Adjust ----------------
            case "brightness_contrast":
                if (e["brightness"] == 0 && e["contrast"] == 0) return "";
                return $"eq=brightness={N(e["brightness"] / 100 * 0.5)}:contrast={N(1 + e["contrast"] / 100)}";

            case "levels":
            {
                double ib = e["in_black"] / 255, iw = Math.Max(ib + 1 / 255.0, e["in_white"] / 255);
                double ob = e["out_black"] / 255, ow = e["out_white"] / 255;
                string levels = $"colorlevels=rimin={N(ib)}:gimin={N(ib)}:bimin={N(ib)}:rimax={N(iw)}:gimax={N(iw)}:bimax={N(iw)}" +
                                $":romin={N(ob)}:gomin={N(ob)}:bomin={N(ob)}:romax={N(ow)}:gomax={N(ow)}:bomax={N(ow)}";
                return Math.Abs(e["gamma"] - 1) > 0.001 ? $"{levels},eq=gamma={N(e["gamma"])}" : levels;
            }

            case "channel_mixer":
                return "colorchannelmixer=" + string.Join(":",
                    new[] { "rr", "rg", "rb", "gr", "gg", "gb", "br", "bg", "bb" }.Select(k => $"{k}={N(e[k] / 100)}"));

            case "proc_amp":
                return $"eq=brightness={N(e["brightness"] / 100 * 0.5)}:contrast={N(1 + e["contrast"] / 100)}," +
                       $"hue=h={N(e["hue"])}:s={N(e["saturation"] / 100)}";

            case "auto_contrast":
                return $"normalize=independence=0:strength={N(e["strength"] / 100)}:smoothing={(int)e["smoothing"]}";

            case "auto_levels":
                return $"normalize=independence=1:strength={N(e["strength"] / 100)}:smoothing={(int)e["smoothing"]}";

            case "auto_color":
                return "grayworld";

            case "extract":
            {
                double b = e["black"], w = Math.Max(b + 1, e["white"]), s = Math.Max(1, e["softness"]);
                return $"hue=s=0,lutyuv=y='255*clip(min((val-{N(b)})/{N(s)},({N(w)}-val)/{N(s)})+1,0,1)':u=128:v=128";
            }

            case "lighting":
                return $"vignette=angle={N(Math.PI / 2 * Math.Clamp(e["darkness"], 1, 100) / 100)}:x0=w*{N(e["x"] / 100)}:y0=h*{N(e["y"] / 100)}";

            // ---------------- Blur & Sharpen ----------------
            case "blur":
                return e["amount"] > 0 ? $"gblur=sigma={N(e["amount"] * 0.3 * S)}" : "";

            case "gaussian_blur":
            {
                double s = e["amount"] * 0.3 * S;
                if (s <= 0) return "";
                return (int)e["dimension"] switch
                {
                    1 => $"gblur=sigma={N(s)}:sigmaV=0.01",
                    2 => $"gblur=sigma=0.01:sigmaV={N(s)}",
                    _ => $"gblur=sigma={N(s)}",
                };
            }

            case "directional_blur":
            {
                int r = Math.Max(1, (int)Math.Round(e["length"] * 0.5 * S));
                if (e["length"] <= 0) return "";
                return (int)e["direction"] switch
                {
                    1 => $"avgblur=sizeX=1:sizeY={r}",
                    2 => $"avgblur=sizeX={r}:sizeY={r}",
                    _ => $"avgblur=sizeX={r}:sizeY=1",
                };
            }

            case "camera_blur":
            {
                double r = e["amount"] * 0.25 * S;
                return r >= 0.5 ? $"boxblur=luma_radius={N(r)}:luma_power=3" : "";
            }

            case "channel_blur":
            {
                // In planar RGB the planes are green (1), blue (2), red (4).
                var parts = new List<string> { "format=gbrap" };
                foreach (var (name, plane) in new[] { ("red", 4), ("green", 1), ("blue", 2) })
                    if (e[name] > 0) parts.Add($"gblur=sigma={N(e[name] * 0.3 * S)}:planes={plane}");
                return parts.Count > 1 ? string.Join(",", parts) : "";
            }

            case "unsharp_mask":
            {
                int size = Math.Clamp(2 * (int)Math.Round(Px("radius")) + 1, 3, 23);
                double amount = Math.Clamp(e["amount"] / 100, 0, 5);
                return amount > 0 ? $"unsharp={size}:{size}:{N(amount)}" : "";
            }

            case "sharpen":
                return e["amount"] > 0 ? $"unsharp=5:5:{N(e["amount"] / 40)}" : "";

            // ---------------- Channel ----------------
            case "invert":
                return (int)e["channel"] switch
                {
                    1 => "lutrgb=r=negval",
                    2 => "lutrgb=g=negval",
                    3 => "lutrgb=b=negval",
                    _ => "negate",
                };

            // ---------------- Color Correction ----------------
            case "tint":
            {
                var (r, g, b) = Rgb(e.Text, "#E0B070");
                double a = e["amount"] / 100;
                return $"hue=s={N(1 - a)},colorchannelmixer=rr={N(1 - a + a * r / 255.0)}:gg={N(1 - a + a * g / 255.0)}:bb={N(1 - a + a * b / 255.0)}";
            }

            case "leave_color":
                return $"colorhold=color={Hex(e.Text, "#E53935")}:similarity={N(Math.Max(0.01, e["similarity"] / 100))}:blend={N(e["blend"] / 100)}";

            case "change_color":
            {
                string colors = new[] { "r", "y", "g", "c", "b", "m" }[Math.Clamp((int)e["colors"], 0, 5)];
                return $"huesaturation=hue={N(e["hue"])}:saturation={N(e["saturation"] / 100)}:colors={colors}";
            }

            case "color_balance_hls":
                return $"hue=h={N(e["hue"])}:s={N(Math.Max(0, 1 + e["saturation"] / 100))}:b={N(e["lightness"] / 10)}";

            case "color_balance":
                return $"colorchannelmixer=rr={N(e["red"] / 100)}:gg={N(e["green"] / 100)}:bb={N(e["blue"] / 100)}";

            case "video_limiter":
                return $"limiter=min={(int)e["min"]}:max={(int)e["max"]}";

            case "lut":
            {
                if (string.IsNullOrEmpty(e.Text) || !File.Exists(e.Text)) return "";
                // FFmpeg wants forward slashes and an escaped drive colon: C\:/Looks/film.cube
                string path = e.Text.Replace('\\', '/').Replace(":", "\\:");
                return $"lut3d=file='{path}'";
            }

            // ---------------- Distort ----------------
            case "mirror":
                return (int)e["side"] switch
                {
                    1 => $"split[{L}a][{L}b];[{L}b]crop=iw/2:ih:iw/2:0,hflip[{L}c];[{L}a][{L}c]overlay=0:0:format=auto",
                    2 => $"split[{L}a][{L}b];[{L}b]crop=iw:ih/2:0:0,vflip[{L}c];[{L}a][{L}c]overlay=0:H/2:format=auto",
                    3 => $"split[{L}a][{L}b];[{L}b]crop=iw:ih/2:0:ih/2,vflip[{L}c];[{L}a][{L}c]overlay=0:0:format=auto",
                    _ => $"split[{L}a][{L}b];[{L}b]crop=iw/2:ih:0:0,hflip[{L}c];[{L}a][{L}c]overlay=W/2:0:format=auto",
                };

            case "lens_distortion":
            {
                double c = e["curvature"] / 100;
                return c == 0 ? "" : $"lenscorrection=k1={N(c * 0.5)}:k2={N(c * 0.15)}:i=bilinear";
            }

            case "spherize":
            {
                double a = e["amount"] / 100;
                return a == 0 ? "" : $"lenscorrection=k1={N(-a * 0.5)}:k2={N(-a * 0.1)}:i=bilinear";
            }

            case "corner_pin":
            {
                string C(string x, double baseValue, string size) => $"{size}*({N(baseValue + e[x] / 100)})";
                return "format=rgba,perspective=" +
                       $"x0='{C("tlx", 0, "W")}':y0='{C("tly", 0, "H")}':x1='{C("trx", 1, "W")}':y1='{C("try", 0, "H")}':" +
                       $"x2='{C("blx", 0, "W")}':y2='{C("bly", 1, "H")}':x3='{C("brx", 1, "W")}':y3='{C("bry", 1, "H")}':" +
                       "sense=destination:interpolation=linear";
            }

            case "wave_warp":
            {
                double amp = Px("height"), length = Math.Max(1, Px("width")), speed = e["speed"] * 2 * Math.PI;
                return amp <= 0 ? "" : Remap("X", $"Y+{N(amp)}*sin(2*PI*X/{N(length)}+T*{N(speed)})");
            }

            case "twirl":
            {
                double angle = e["angle"] * Math.PI / 180, radius = Math.Max(0.01, e["radius"] / 100);
                if (angle == 0) return "";
                string prelude = $"st(1,X-W/2);st(2,Y-H/2);st(3,{N(angle)}*pow(max(0,1-hypot(ld(1),ld(2))/({N(radius)}*min(W,H)/2)),2));";
                return Remap("W/2+ld(1)*cos(ld(3))-ld(2)*sin(ld(3))", "H/2+ld(1)*sin(ld(3))+ld(2)*cos(ld(3))", prelude);
            }

            case "offset":
                return Remap($"mod(X-W*{N(e["x"] / 100)}+W*4,W)", $"mod(Y-H*{N(e["y"] / 100)}+H*4,H)");

            case "magnify":
            {
                double zoom = Math.Max(1, e["zoom"] / 100);
                string prelude = $"st(1,X-W*{N(e["x"] / 100)});st(2,Y-H*{N(e["y"] / 100)});st(3,lt(hypot(ld(1),ld(2)),H*{N(e["size"] / 100)}));";
                return Remap($"if(ld(3),W*{N(e["x"] / 100)}+ld(1)/{N(zoom)},X)", $"if(ld(3),H*{N(e["y"] / 100)}+ld(2)/{N(zoom)},Y)", prelude);
            }

            case "turbulent_displace":
            {
                double amount = Px("amount"), size = Math.Max(1, Px("size")), speed = e["speed"];
                if (amount <= 0) return "";
                return Remap($"X+{N(amount)}*sin(Y/{N(size)}+T*{N(speed)})*cos(X/{N(size * 1.3)})",
                             $"Y+{N(amount)}*cos(X/{N(size)}-T*{N(speed)})*sin(Y/{N(size * 0.7)})");
            }

            case "warp_stabilizer":
            {
                int range = Math.Clamp((int)Math.Round(e["range"]), 4, 64);
                return $"deshake=rx={range}:ry={range}:edge=mirror";
            }

            // ---------------- Generate ----------------
            case "grid":
            {
                int size = Math.Max(2, (int)Math.Round(Px("size"))), line = Math.Max(1, (int)Math.Round(Px("line")));
                return $"drawgrid=w={size}:h={size}:t={line}:c={Hex(e.Text, "#FFFFFF")}@{N(e["opacity"] / 100)}";
            }

            case "checkerboard":
            {
                double size = Math.Max(2, Px("size"));
                return Paint(Rgb(e.Text, "#000000"), $"mod(floor(X/{N(size)})+floor(Y/{N(size)}),2)*{N(e["opacity"] / 100)}");
            }

            case "ramp":
            {
                string along = (int)e["direction"] switch { 1 => "Y/H", 2 => "1-X/W", 3 => "X/W", _ => "1-Y/H" };
                return Paint(Rgb(e.Text, "#1E3A8A"), $"clip({along},0,1)*{N(e["opacity"] / 100)}");
            }

            case "circle":
            {
                double feather = Math.Max(0.5, Px("feather"));
                string inside = $"clip((H*{N(e["radius"] / 100)}-hypot(X-W*{N(e["x"] / 100)},Y-H*{N(e["y"] / 100)}))/{N(feather)}+0.5,0,1)";
                return Paint(Rgb(e.Text, "#FFFFFF"), $"{inside}*{N(e["opacity"] / 100)}");
            }

            case "lens_flare":
            {
                double k = e["brightness"] / 100;
                string glow = $"st(1,{N(k)}*exp(-pow(hypot(X-W*{N(e["x"] / 100)},Y-H*{N(e["y"] / 100)})/(0.22*H),2)));";
                string P(string plane, int warm) => $"'{glow}min(255,{plane}(X,Y)+{warm}*ld(1))'";
                return $"format=gbrap,geq=r={P("r", 255)}:g={P("g", 230)}:b={P("b", 190)}:a='alpha(X,Y)'";
            }

            // ---------------- Image Control ----------------
            case "black_white":
                return "hue=s=0";

            case "gamma":
                return Math.Abs(e["gamma"] - 1) > 0.001 ? $"eq=gamma={N(e["gamma"])}" : "";

            // ---------------- Keying ----------------
            case "ultrakey":
            {
                var (r, g, b) = Rgb(e.Text, "#00FF00");
                string key = $"format=yuva444p,chromakey=color={Hex(e.Text, "#00FF00")}:similarity={N(Math.Max(0.01, e["similarity"] / 100))}:blend={N(e["blend"] / 100)}";
                bool blueScreen = b > g;
                return e["despill"] >= 0.5 ? $"{key},format=gbrap,despill=type={(blueScreen ? "blue" : "green")}" : key;
            }

            case "chromakey":
                return $"format=rgba,colorkey=color={Hex(e.Text, "#00FF00")}:similarity={N(Math.Max(0.01, e["similarity"] / 100))}:blend={N(e["blend"] / 100)}";

            case "luma_key":
            {
                double threshold = (int)e["key"] == 1 ? 1 : 0;
                return $"format=yuva444p,lumakey=threshold={N(threshold)}:tolerance={N(Math.Max(0.01, e["tolerance"] / 100))}:softness={N(e["softness"] / 100)}";
            }

            case "alpha_adjust":
            {
                double o = e["opacity"] / 100;
                return $"format=rgba,lutrgb=a='{(e["invert"] >= 0.5 ? "negval" : "val")}*{N(o)}'";
            }

            // ---------------- Noise & Grain ----------------
            case "noise":
            {
                int amount = (int)Math.Round(e["amount"]);
                if (amount <= 0) return "";
                return e["color"] >= 0.5 ? $"noise=alls={amount}:allf=t" : $"format=yuva444p,noise=c0s={amount}:c0f=t";
            }

            case "median":
                return $"median=radius={Math.Clamp((int)Math.Round(Px("radius")), 1, 127)}";

            case "dust_scratches":
                return $"median=radius={Math.Clamp((int)Math.Round(Px("radius")), 1, 127)},unsharp=3:3:0.6";

            // ---------------- Perspective ----------------
            case "drop_shadow":
            {
                double angle = e["direction"] * Math.PI / 180, d = Px("distance");
                int dx = (int)Math.Round(Math.Sin(angle) * d), dy = (int)Math.Round(-Math.Cos(angle) * d);
                double soft = Px("softness") * 0.5;
                string blur = soft > 0.2 ? $",gblur=sigma={N(soft)}" : "";
                return $"format=rgba,split[{L}a][{L}b];" +
                       $"[{L}a]colorchannelmixer=rr=0:rg=0:rb=0:gr=0:gg=0:gb=0:br=0:bg=0:bb=0:aa={N(e["opacity"] / 100)}{blur}," +
                       $"crop=w=iw-{Math.Abs(dx)}:h=ih-{Math.Abs(dy)}:x={Math.Max(0, -dx)}:y={Math.Max(0, -dy)}," +
                       $"pad=w=iw+{Math.Abs(dx)}:h=ih+{Math.Abs(dy)}:x={Math.Max(0, dx)}:y={Math.Max(0, dy)}:color=black@0[{L}s];" +
                       $"[{L}s][{L}b]overlay=format=auto";
            }

            case "basic_3d":
                return "format=rgba,perspective=" + Basic3D(e["swivel"], e["tilt"], e["distance"]) + ":sense=destination:interpolation=linear";

            // ---------------- Stylize ----------------
            case "vignette":
                return e["amount"] > 0 ? $"vignette=angle={N(Math.PI / 2 * e["amount"] / 100)}" : "";

            case "emboss":
            {
                double s = e["strength"] / 100;
                return s <= 0 ? "" : "hue=s=0," + Convolve([-s, -s, 0, -s, 0, s, 0, s, s], 128);
            }

            case "color_emboss":
            {
                double s = e["strength"] / 100;
                return s <= 0 ? "" : Convolve([-s, -s, 0, -s, 1, s, 0, s, s], 0);
            }

            case "find_edges":
                return "edgedetect=mode=colormix:high=0.2:low=0.05" + (e["invert"] >= 0.5 ? ",negate" : "");

            case "mosaic":
            {
                int block = Math.Max(1, (int)Math.Round(Px("block")));
                return block <= 1 ? "" : $"pixelize=w={block}:h={block}";
            }

            case "posterize":
            {
                double step = 255 / Math.Max(1, e["levels"] - 1);
                string q = $"'floor(val/{N(step)}+0.5)*{N(step)}'";
                return $"lutrgb=r={q}:g={q}:b={q}";
            }

            case "solarize":
            {
                string q = $"'if(gt(val,{N(e["threshold"])}),255-val,val)'";
                return $"lutrgb=r={q}:g={q}:b={q}";
            }

            case "threshold":
            {
                string q = $"'if(gt(val,{N(e["level"])}),255,0)'";
                return $"hue=s=0,lutrgb=r={q}:g={q}:b={q}";
            }

            case "strobe":
            {
                double period = Math.Max(0.04, e["period"]), flash = Math.Clamp(e["duration"], 0.01, period);
                return $"drawbox=x=0:y=0:w=iw:h=ih:color={Hex(e.Text, "#FFFFFF")}@{N(e["opacity"] / 100)}:t=fill:enable='lt(mod(t,{N(period)}),{N(flash)})'";
            }

            case "replicate":
            {
                int n = Math.Clamp((int)Math.Round(e["count"]), 2, 4);
                var copies = Enumerable.Range(0, n * n).Select(i => $"[{L}c{i}]").ToList();
                var rows = new List<string>();
                var chain = new List<string> { $"scale=trunc(iw/{n}/2)*2:trunc(ih/{n}/2)*2,split={n * n}{string.Concat(copies)}" };
                for (int r = 0; r < n; r++)
                {
                    chain.Add($"{string.Concat(copies.Skip(r * n).Take(n))}hstack=inputs={n}[{L}r{r}]");
                    rows.Add($"[{L}r{r}]");
                }
                chain.Add($"{string.Concat(rows)}vstack=inputs={n}");
                return string.Join(";", chain);
            }

            // ---------------- Time ----------------
            case "echo":
            {
                int echoes = Math.Clamp((int)Math.Round(e["echoes"]), 1, 8);
                double decay = Math.Clamp(e["decay"] / 100, 0.05, 1);
                string weights = string.Join(" ", Enumerable.Range(0, echoes + 1).Select(i => N(Math.Pow(decay, i))));
                return $"tmix=frames={echoes + 1}:weights='{weights}'";
            }

            case "posterize_time":
            {
                double rate = Math.Clamp(e["rate"], 1, ctx.Fps);
                return rate >= ctx.Fps - 0.01 ? "" : $"fps={N(rate)},fps={N(ctx.Fps)}";
            }

            // ---------------- Transform ----------------
            case "flip":
                return string.Join(",", new[] { e["horizontal"] >= 0.5 ? "hflip" : "", e["vertical"] >= 0.5 ? "vflip" : "" }.Where(s => s.Length > 0));

            case "hflip":
                return "hflip";

            case "vflip":
                return "vflip";

            case "crop":
            {
                double l = e["left"] / 100, r = e["right"] / 100, t = e["top"] / 100, b = e["bottom"] / 100;
                if (l + r + t + b <= 0) return "";
                double kw = Math.Max(0.02, 1 - l - r), kh = Math.Max(0.02, 1 - t - b);
                // Cut the edges off, then put the picture back on a see-through canvas of the
                // original size, so it stays in the same place and lower tracks show at the edges.
                return $"format=rgba,crop=w=iw*{N(kw)}:h=ih*{N(kh)}:x=iw*{N(l)}:y=ih*{N(t)}" +
                       $",pad=w=iw/{N(kw)}:h=ih/{N(kh)}:x=ow*{N(l)}:y=oh*{N(t)}:color=black@0";
            }

            case "edge_feather":
            {
                double f = Math.Max(1, Px("amount"));
                return Alpha($"clip(min(min(X,W-1-X),min(Y,H-1-Y))/{N(f)},0,1)");
            }

            // ---------------- Transition (as effects) ----------------
            case "linear_wipe":
                return WipeAway(Reveal.Linear(e["angle"]), e["completion"], Px("feather") / (1080 * S));

            case "radial_wipe":
                return WipeAway(Reveal.Clock(e["start"]), e["completion"], 0.004);

            case "venetian_blinds":
                return WipeAway(Reveal.Blinds(Math.Max(1, (int)e["bands"]), (int)e["direction"] == 1), e["completion"], 0.01);

            case "block_dissolve":
            {
                string block = N(Math.Max(1, Px("block")));
                return WipeAway(Reveal.Blocks(block, block), e["completion"], 0.002);
            }

            default:
                return "";
        }
    }

    /// <summary>The clip disappears where the wipe has passed (completion 0 = all there, 100 = all gone).</summary>
    private static string WipeAway(string shape, double completion, double feather)
    {
        if (completion <= 0) return "";
        return Alpha($"1-{Reveal.Appeared(shape, N(completion / 100), feather)}");
    }

    /// <summary>
    /// Basic 3D: the four corners of the picture after turning it (swivel around the upright
    /// axis, tilt around the level axis), seen through a camera. Written with W and H so it
    /// works at any size.
    /// </summary>
    private static string Basic3D(double swivelDegrees, double tiltDegrees, double distancePercent)
    {
        double sw = swivelDegrees * Math.PI / 180, ti = tiltDegrees * Math.PI / 180;
        double camera = 2.5 * Math.Max(0.5, distancePercent / 100); // in picture widths
        var corners = new (double X, double Y)[] { (-0.5, -0.5), (0.5, -0.5), (-0.5, 0.5), (0.5, 0.5) };
        var parts = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            var (x, y) = corners[i];
            // x and y are in picture widths/heights; z in picture widths. Work in "W units":
            // the y coordinate is H/W times as long. Use a symbolic H/W ratio in the expression.
            double x1 = x * Math.Cos(sw), z1 = x * Math.Sin(sw);
            // y in W units = y*H/W; tilt mixes y and z.
            string yW = $"({N(y)}*H/W)";
            string y2 = $"({yW}*{N(Math.Cos(ti))}-{N(z1)}*{N(Math.Sin(ti))})";
            string z2 = $"({yW}*{N(Math.Sin(ti))}+{N(z1)}*{N(Math.Cos(ti))})";
            string k = $"({N(camera)}/({N(camera)}+{z2}))";
            parts.Add($"x{i}='W/2+W*{N(x1)}*{k}':y{i}='H/2+W*{y2}*{k}'");
        }
        return string.Join(":", parts);
    }
}
