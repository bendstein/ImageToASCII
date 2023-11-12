using LibI2A.Converter;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LibI2A.Common;

public static class Utils
{
    const string GLYPHS_SEPARATOR = " ; ";
    const string GLYPHS_ARR_SEPARATOR = " , ";

    public static void WriteGlyphsFile(Glyph[] glyphs, Stream stream)
    {
        bool is_first = true;

        foreach (var glyph in glyphs)
        {
            string line = glyph.SSIM.HasValue ? 
                $"{glyph.Symbol}{GLYPHS_SEPARATOR}{glyph.SSIM.Value.MeanLuminance:0.00}{GLYPHS_SEPARATOR}{glyph.SSIM.Value.StdDevLuminance:0.00}"
                    + $"{GLYPHS_SEPARATOR}{glyph.SSIM.Value.Luminances.Select(l => (l?? glyph.SSIM.Value.MeanLuminance).ToString("0.00")).AggregateOrDefault((a, b) => $"{a}{GLYPHS_ARR_SEPARATOR}{b}", string.Empty)}"
                : glyph.Symbol.ToString();
            var line_bytes = Encoding.ASCII.GetBytes($"{(is_first? string.Empty : "\r\n")}{line}");
            stream.Write(line_bytes);

            is_first = false;
        }

        //using var sw = new StreamWriter(stream);

        //foreach(var glyph in glyphs)
        //{
        //    if(glyph.SSIM.HasValue)
        //    {
        //        sw.WriteLine($"{glyph.Symbol}{GLYPHS_SEPARATOR}{glyph.SSIM.Value.MeanLuminance:0.00}{GLYPHS_SEPARATOR}{glyph.SSIM.Value.StdDevLuminance:0.00}"
        //            + $"{GLYPHS_SEPARATOR}{glyph.SSIM.Value.Luminances.Select(l => l.ToString("0.00")).Aggregate(string.Empty, (a, b) => $"{a}{GLYPHS_ARR_SEPARATOR}{b}")}"
        //            + $"{GLYPHS_SEPARATOR}{glyph.SSIM.Value.Structures.Select(l => l.ToString("0.00")).Aggregate(string.Empty, (a, b) => $"{a}{GLYPHS_ARR_SEPARATOR}{b}")}");
        //    }
        //    else
        //    {
        //        sw.WriteLine(glyph.Symbol);
        //    }
        //}
    }

    public static Glyph[] ReadGlyphsFile(Stream stream)
    {
        using var sr = new StreamReader(stream);

        var glyphs = sr.ReadToEnd().Split('\n')
            .Where(line => line.Length > 0)
            .Select(line => new Glyph(line[0]))
            .DistinctBy(g => g.Symbol)
            .ToArray();

        return glyphs;
    }

    internal static T[] StretchArray<T>(T[] array, int new_size)
    {
        if (new_size <= array.Length)
            return array;
        else if (new_size % array.Length != 0)
            throw new Exception($"new_size {new_size} must be an integer multiple of {array.Length}");

        T[] rv = new T[new_size];
        var factor = new_size / array.Length;

        for(int i = 0, k = 0; i < array.Length; i++)
        {
            for(int j = 0; j < factor; j++)
            {
                rv[k++] = array[i];
            }
        }

        return rv;
    }

    internal static double Truncate(double d, int precision)
    {
        var factor = Math.Pow(10, precision);
        return Math.Floor(d * factor) / factor;
    }
}