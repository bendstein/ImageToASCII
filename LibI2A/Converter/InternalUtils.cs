using ImageMagick;

namespace LibI2A.Converter;

public static class InternalUtils
{
    public static double Modulo(double r, double m) => ((r % m) + m) % m;

    public static T AggregateOrDefault<T>(this IEnumerable<T> source, Func<T, T, T> func, T def)
        => (source == null || !source.Any()) ? def : source.Aggregate(func);

    public static (uint a, double h, double s, double v) ARGBToAHSV((uint a, uint r, uint g, uint b) argb)
    {
        (double R, double G, double B) = (argb.r / 255f, argb.g / 255f, argb.b / 255f);
        double XPMin = Math.Min(R, Math.Min(G, B));
        double XPMax = Math.Max(R, Math.Max(G, B));
        double XPD = XPMax - XPMin;

        (double h, double s, double v) = (0, 0, XPMax);

        //Not greyscale, set chroma data
        if (XPD != 0)
        {
            s = XPD / XPMax;

            double delta_r = (((XPMax - R) / 6) + (XPMax / 2)) / XPMax;
            double delta_g = (((XPMax - G) / 6) + (XPMax / 2)) / XPMax;
            double delta_b = (((XPMax - B) / 6) + (XPMax / 2)) / XPMax;

            if (R == XPMax)
                h = delta_b - delta_g;
            else if (G == XPMax)
                h = (1f / 3f) + delta_r - delta_b;
            else if (B == XPMax)
                h = (2f / 3f) + delta_g - delta_r;

            if (h < 0)
                h += 1;
            if (h > 1)
                h -= 1;
        }

        return (argb.a, h, s, v);
    }

    public static (uint a, uint r, uint g, uint b) AHSVToARGB((uint a, double h, double s, double v) ahsv)
    {
        uint a = ahsv.a;
        double r, g, b;

        //If saturation is 0, no chroma
        if (ahsv.s == 0)
        {
            (r, g, b) = (ahsv.v, ahsv.v, ahsv.v);
        }
        else
        {
            var h = ahsv.h * 6;
            if (h == 6)
                h = 0;

            var i = (int)Math.Floor(h);

            var v0 = ahsv.v;
            var v1 = ahsv.v * (1 - ahsv.s);
            var v2 = ahsv.v * (1 - ahsv.s * (h - i));
            var v3 = ahsv.v * (1 - ahsv.s * (1 - (h - i)));

            (r, g, b) = i switch
            {
                0 => (v0, v3, v1),
                1 => (v2, v0, v1),
                2 => (v1, v0, v3),
                3 => (v1, v2, v0),
                4 => (v3, v1, v0),
                _ => (v0, v1, v2),
            };
        }

        return (
            a,
            (uint)(r * 255),
            (uint)(g * 255),
            (uint)(b * 255)
        );
    }

    public static double GetLuminance((uint a, uint r, uint g, uint b) argb)
    {
        //Magic numbers
        const double
            LINEAR_POWER = 2.2f,
            COEFF_R = 0.2126f,
            COEFF_G = 0.7152f,
            COEFF_B = 0.0722f;

        //Linearize each component
        //(RGB is encoded in a power curve whereas luminance is linear)
        (var r_lin, var g_lin, var b_lin) = (Math.Pow(argb.r / (float)0xFF, LINEAR_POWER), Math.Pow(argb.g / (float)0xFF, LINEAR_POWER), Math.Pow(argb.b / (float)0xFF, LINEAR_POWER));

        //Calculate luminance
        var y = COEFF_R * r_lin + COEFF_G * g_lin + COEFF_B * b_lin;

        return y / 0xFF;
    }

    internal static byte ScaleUShort(ushort n)
        => (byte)Math.Min(((float)n / ushort.MaxValue) * byte.MaxValue, byte.MaxValue);

    internal static (uint a, uint r, uint g, uint b) ToARGB(uint color) => ((color >> 24) & 0xFF, (color >> 16) & 0xFF, (color >> 8) & 0xFF, color & 0xFF);

    internal static uint ToUInt((uint a, uint r, uint g, uint b) ARGB) => (ARGB.a << 24) + (ARGB.r << 16) + (ARGB.g << 8) + (ARGB.b);

    internal static (uint a, uint r, uint g, uint b) CoalescePixel(IPixel<ushort> pixel, Stack<IMagickImage<ushort>> previousLayers)
    {
        var pixel_color = pixel.ToColor();

        if (pixel_color == null)
            return (0, 0, 0, 0);

        (uint a, uint r, uint g, uint b) = (ScaleUShort(pixel_color.A), ScaleUShort(pixel_color.R), ScaleUShort(pixel_color.G), ScaleUShort(pixel_color.B));

        Stack<IMagickImage<ushort>> prevLayersTemp = new();

        //If color has any transparency, combine with previous layer
        while (a < 0xFF && previousLayers.TryPop(out var prev_image))
        {
            prevLayersTemp.Push(prev_image);
            var prev_pixel = prev_image.GetPixels().Where(p => p.X == pixel.X && p.Y == pixel.Y).FirstOrDefault();
            var prev_color = prev_pixel?.ToColor();

            if (prev_color != null)
            {
                (uint prev_a, uint prev_r, uint prev_g, uint prev_b) = (ScaleUShort(prev_color.A), ScaleUShort(prev_color.R), ScaleUShort(prev_color.G), ScaleUShort(prev_color.B));

                var ratio = (float)a / 0xFF;

                a = prev_a + (uint)Math.Floor(ratio * (0xFF - prev_a));
                r = (uint)Math.Floor(((1f - ratio) * prev_r) + (ratio * r));
                g = (uint)Math.Floor(((1f - ratio) * prev_g) + (ratio * g));
                b = (uint)Math.Floor(((1f - ratio) * prev_b) + (ratio * b));
            }
        }

        //Push previous images back to stack
        while (prevLayersTemp.TryPop(out var layer))
            previousLayers.Push(layer);

        return (a, r, g, b);
    }

    internal static T[] StretchArray<T>(T[] array, int new_size)
    {
        if (new_size <= array.Length)
            return array;
        else if (new_size % array.Length != 0)
            throw new Exception($"new_size {new_size} must be an integer multiple of {array.Length}");

        T[] rv = new T[new_size];
        var factor = new_size / array.Length;

        for (int i = 0, k = 0; i < array.Length; i++)
        {
            for (int j = 0; j < factor; j++)
            {
                rv[k++] = array[i];
            }
        }

        return rv;
    }

    public static double Truncate(double d, int precision)
    {
        var factor = Math.Pow(10, precision);
        return Math.Floor(d * factor) / factor;
    }

    public static double Round(double d, int precision)
    {
        var factor = Math.Pow(10, precision);
        return Math.Round(d * factor) / factor;
    }

    internal static Dictionary<string, IMagickImage<ushort>> GetGlyphImages(string[] glyphs, int font_point_size, string font_face, bool invert)
    {
        return glyphs.ToDictionary(g => g, g =>
        {
            IMagickImage<ushort> glyph_image = new MagickImage(invert? MagickColors.Black : MagickColors.White, font_point_size, font_point_size)
            {
                ColorSpace = ColorSpace.sRGB,
                ColorType = ColorType.TrueColor,
                Format = MagickFormat.Jpeg
            };

            new Drawables()
                .FontPointSize(font_point_size)
                .Font(font_face, FontStyleType.Normal, FontWeight.Thin, FontStretch.Normal)
                .StrokeColor(invert ? MagickColors.White : MagickColors.Black)
                .FillColor(invert ? MagickColors.White : MagickColors.Black)
                .Gravity(Gravity.Center)
                .Text(0, 0, g)
                .Draw(glyph_image);

            return glyph_image;
        });
    }
}