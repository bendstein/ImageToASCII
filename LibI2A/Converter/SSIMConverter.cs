using ImageMagick;
using LibI2A.Common;
using LibI2A.Common.Extensions;
using LibI2A.Converter;
using LibI2A.Database;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace LibI2A.Converters;

/// <summary>
/// Use the structural similarity index to find the most similar glyph to each pixel (or group of pixels),
/// and write the resulting glyph
/// </summary>
public class SSIMConverter : IImageToASCIIConverter
{
    /// <summary>
    /// Codebook of glyphs for comparison
    /// </summary>
    public Glyph[] Glyphs { get; set; } = Array.Empty<Glyph>();

    /// <summary>
    /// Connection to DB containing cached values
    /// </summary>
    public SqliteConnection Connection { get; set; }

    /// <summary>
    /// Settings to modify image conversion
    /// </summary>
    public SSIMSettings Settings { get; set; } = new();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="glyphs">Codebook of glyphs for comparison</param>
    /// <param name="connection">Connection to DB containing cached values</param>
    /// <param name="settings">Settings to modify image conversion</param>
    public SSIMConverter(Glyph[] glyphs, SqliteConnection connection, SSIMSettings? settings = null)
    {
        Glyphs = glyphs;
        Connection = connection;
        Settings = settings ?? new();
    }

    public void ConvertImage(Stream input, Stream output, 
        Func<string, uint, string>? before_write = null, Action<string, uint>? after_write = null)
    {
        MagickImageCollection images = new(input);

        var supports_multiframe = images.FirstOrDefault()?.FormatInfo?.SupportsMultipleFrames?? false;

        long position = output.CanSeek? output.Position : 0;

        using var sw = new StreamWriter(output);

        Dictionary<SSIM, Glyph> cache = new();

        int repeat_count = 0;
        int repeat = supports_multiframe && images.Count > 1? Settings.Repeat : 0;

        var previous = new string[images.Count];

        //If animation isn't supported, and this has multiple images, treat as layers. Render only the top layer,
        //coalescing to lower layers when there is any transparency
        Stack<IMagickImage<ushort>> image_stack = new();
        if(images.Count > 1 && !supports_multiframe)
        {
            for(int i = 0; i < images.Count - 1; i++)
            {
                var image = images[i];
                image_stack.Push(image);
            }
            images = new(new IMagickImage<ushort>[] { images.Last() });
        }

        while (repeat < 0 || repeat_count <= repeat)
        {
            int ndx = 0;

            foreach (var image in images)
            {
                try
                {
                    StringBuilder sb = new();

                    //Delay between frames
                    if (images.Count > 1 && image.AnimationDelay > 0)
                        Thread.Sleep(image.AnimationDelay * 10);

                    //On next frame, return to start position
                    if (output.CanSeek)
                    {
                        output.Seek(position, SeekOrigin.Begin);
                    }
                    //If not seekable, and ANSI escape sequences are enabled, write sequence to return to start
                    else if (Settings.AllowANSIEscapes)
                    {
                        sw.Write($"\x1b[{image.Page.X};{image.Page.Y}H");
                        sw.Flush();
                    }
                    //Otherwise, leave a line of padding
                    else if (ndx > 0)
                    {
                        sw.WriteLine();
                        sw.Flush();
                    }

                    if (repeat_count > 0 && !string.IsNullOrEmpty(previous[ndx]))
                    {
                        sw.Write(previous[ndx++]);
                        sw.Flush();
                        continue;
                    }

                    switch (image.Format)
                    {
                        case MagickFormat.Jpeg:
                        case MagickFormat.Jpg:
                            image.Quality = 50;
                            break;
                    }

                    image.SetBitDepth(8);

                    if (Settings.Clamp.w != null || Settings.Clamp.h != null)
                    {
                        if (Settings.Clamp.h.HasValue && Settings.Clamp.h.Value < image.Height && Settings.Clamp.h.Value > 0)
                            image.Resize(0, Settings.Clamp.h.Value);
                        if (Settings.Clamp.w.HasValue && Settings.Clamp.w.Value < image.Width && Settings.Clamp.w.Value > 0)
                            image.Resize(Settings.Clamp.w.Value, 0);
                    }

                    var image_ssim_values = SSIMUtils.CalculateSSIMValues(image, image_stack, Settings.Window.w, Settings.Window.h)
                        .Select(pair =>
                        {
                            var tile = pair.tile;
                            var values = pair.values;

                            if (Settings.Precision >= 0)
                            {
                                values.MeanLuminance = Utils.Truncate(values.MeanLuminance, Settings.Precision);
                                values.StdDevLuminance = Utils.Truncate(values.StdDevLuminance, Settings.Precision);
                                for (int i = 0; i < values.Luminances.Length; i++)
                                {
                                    var luminance = values.Luminances[i];
                                    if (luminance != null)
                                        values.Luminances[i] = Utils.Truncate(luminance.Value, Settings.Precision);
                                }
                            }

                            return (tile, values);
                        });

                    int i = 0;

                    var bits = (int)Math.Ceiling(Math.Pow(2, image.DetermineBitDepth()));

                    foreach (var tile in image_ssim_values)
                    {
                        Glyph glyph = new();
                        Thread getGlyph = new(() =>
                        {
                            if (!cache.TryGetValue(tile.values, out glyph))
                            {
                                //If not in cache dictionary, check db
                                if (DBUtils.TryGetMemoizedGlpyh(Connection,
                                    tile.values.Luminances,
                                    Settings.Window,
                                    bits,
                                    Settings.coeffs,
                                    Settings.weights,
                                    out var glyph_string) && !string.IsNullOrWhiteSpace(glyph_string))
                                {
                                    glyph.Symbol = glyph_string[0];
                                    cache[tile.values] = glyph;
                                }
                                else
                                {
                                    //Not cached, calculate and add to cache
                                    SemaphoreSlim mutex = new(1, 1);
                                    List<(Glyph g, SSIMComparison c)> comparisons = new();

                                    const int THREAD_COUNT = 8;
                                    List<Thread> current_threads = new();

                                    void JoinThreads()
                                    {
                                        foreach (var thread in current_threads)
                                            thread.Join();
                                        current_threads.Clear();
                                    }

                                    foreach (var g in Glyphs)
                                    {
                                        Thread compare = new(() =>
                                        {
                                            var value = SSIMUtils.CompareSSIMs(tile.values, g.SSIM!.Value,
                                                Settings.coeffs,
                                                Settings.weights,
                                                bits);

                                            mutex.Wait();

                                            try
                                            {
                                                comparisons.Add((g, value));
                                            }
                                            finally
                                            {
                                                mutex.Release();
                                            }
                                        });
                                        compare.Start();

                                        current_threads.Add(compare);

                                        if (current_threads.Count >= THREAD_COUNT)
                                            JoinThreads();
                                    }

                                    if (current_threads.Any())
                                        JoinThreads();

                                    glyph = comparisons.MaxBy(c => c.c.Index).g;

                                    //Add glyph to dictionary and db
                                    cache[tile.values] = glyph;

                                    DBUtils.WriteMemoizedGlyph(Connection,
                                        tile.values.Luminances,
                                        Settings.Window,
                                        bits,
                                        Settings.coeffs,
                                        Settings.weights,
                                        glyph.Symbol.ToString());
                                }
                            }
                        });

                        getGlyph.Start();

                        (uint a_avg, double h_avg, double s_avg, double v_avg) = (0, 0d, 0d, 0d);

                        foreach (var pixel in tile.tile)
                        {
                            var color = pixel?.ToColor();
                            if (color != null)
                            {
                                (uint a, uint r, uint g, uint b) argb;

                                //Coalesce transparency
                                if (color.A < ushort.MaxValue && image_stack.Any())
                                {
                                    argb = InternalUtils.CoalescePixel(pixel!, image_stack);
                                }
                                else
                                {
                                    argb = (
                                       InternalUtils.ScaleUShort(color.A),
                                       InternalUtils.ScaleUShort(color.R),
                                       InternalUtils.ScaleUShort(color.G),
                                       InternalUtils.ScaleUShort(color.B));
                                }

                                (uint a, double h, double s, double v) ahsv = InternalUtils.ARGBToAHSV(argb);
                                a_avg += ahsv.a;
                                h_avg += ahsv.h;
                                s_avg += ahsv.s;
                                v_avg += ahsv.v;
                            }
                        }

                        a_avg /= (uint)tile.tile.Count();
                        h_avg /= (uint)tile.tile.Count();
                        s_avg /= (uint)tile.tile.Count();
                        v_avg /= (uint)tile.tile.Count();

                        (a_avg, uint r_avg, uint g_avg, uint b_avg) = InternalUtils.AHSVToARGB((a_avg, h_avg, s_avg, v_avg));

                        var avg_color = InternalUtils.ToUInt((a_avg, r_avg, g_avg, b_avg));

                        //avg_color /= (uint)tile.tile.Count();

                        getGlyph.Join();

                        string s = glyph.Symbol.ToString();

                        if (before_write != null)
                            s = before_write(s, avg_color);

                        sb.Append(s);

                        if (!Settings.WriteAll)
                        {
                            sw.Write(s);
                        }

                        i += Settings.Window.w;
                        if (i >= image.Width)
                        {
                            i = 0;

                            sb.AppendLine();

                            if (!Settings.WriteAll)
                            {
                                sw.WriteLine();
                                output.Flush();
                            }
                        }

                        if (after_write != null)
                            after_write(s, avg_color);
                    }

                    previous[ndx++] = sb.ToString();

                    if (Settings.WriteAll)
                    {
                        sw.Write(sb.ToString());
                        sw.Flush();
                    }
                }
                finally
                {
                    image_stack.Push(image);
                }
            }

            sw.Flush();

            repeat_count++;
        }
    }

    public void ConvertImage2(Stream input, IGlyphWriter writer)
    {
        MagickImageCollection images = new(input);

        var supports_multiframe = images.FirstOrDefault()?.FormatInfo?.SupportsMultipleFrames ?? false;

        Dictionary<SSIM, Glyph> cache = new();

        int repeat_count = 0;
        int repeat = supports_multiframe && images.Count > 1 ? Settings.Repeat : 0;

        var previous = new string[images.Count];

        //If animation isn't supported, and this has multiple images, treat as layers. Render only the top layer,
        //coalescing to lower layers when there is any transparency
        Stack<IMagickImage<ushort>> image_stack = new();
        if (images.Count > 1 && !supports_multiframe)
        {
            for (int i = 0; i < images.Count - 1; i++)
            {
                var image = images[i];
                image_stack.Push(image);
            }
            images = new(new IMagickImage<ushort>[] { images.Last() });
        }

        while (repeat < 0 || repeat_count <= repeat)
        {
            int ndx = 0;

            foreach (var image in images)
            {
                try
                {
                    StringBuilder sb = new();

                    //Delay between frames
                    if (images.Count > 1 && image.AnimationDelay > 0)
                        Thread.Sleep(image.AnimationDelay * 10);

                    //If seeking is enabled, jump to position, otherwise write in a line of padding
                    if(writer.SeekEnabled)
                    {
                        writer.Seek(image.Page.X, image.Page.Y);
                    }
                    else if(writer.SeekLinearEnabled)
                    {

                    }
                    else
                    {
                        writer.WriteLine();
                        writer.Flush();
                    }

                    if (repeat_count > 0 && !string.IsNullOrEmpty(previous[ndx]))
                    {
                        writer.Write(previous[ndx++]);
                        continue;
                    }

                    switch (image.Format)
                    {
                        case MagickFormat.Jpeg:
                        case MagickFormat.Jpg:
                            image.Quality = 50;
                            break;
                    }

                    image.SetBitDepth(8);

                    if (Settings.Clamp.w != null || Settings.Clamp.h != null)
                    {
                        if (Settings.Clamp.h.HasValue && Settings.Clamp.h.Value < image.Height && Settings.Clamp.h.Value > 0)
                            image.Resize(0, Settings.Clamp.h.Value);
                        if (Settings.Clamp.w.HasValue && Settings.Clamp.w.Value < image.Width && Settings.Clamp.w.Value > 0)
                            image.Resize(Settings.Clamp.w.Value, 0);
                    }

                    var image_ssim_values = SSIMUtils.CalculateSSIMValues(image, image_stack, Settings.Window.w, Settings.Window.h)
                        .Select(pair =>
                        {
                            var tile = pair.tile;
                            var values = pair.values;

                            if (Settings.Precision >= 0)
                            {
                                values.MeanLuminance = Utils.Truncate(values.MeanLuminance, Settings.Precision);
                                values.StdDevLuminance = Utils.Truncate(values.StdDevLuminance, Settings.Precision);
                                for (int i = 0; i < values.Luminances.Length; i++)
                                {
                                    var luminance = values.Luminances[i];
                                    if (luminance != null)
                                        values.Luminances[i] = Utils.Truncate(luminance.Value, Settings.Precision);
                                }
                            }

                            return (tile, values);
                        });

                    int i = 0;

                    var bits = (int)Math.Ceiling(Math.Pow(2, image.DetermineBitDepth()));

                    foreach (var tile in image_ssim_values)
                    {
                        Glyph glyph = new();
                        Thread getGlyph = new(() =>
                        {
                            if (!cache.TryGetValue(tile.values, out glyph))
                            {
                                //If not in cache dictionary, check db
                                if (DBUtils.TryGetMemoizedGlpyh(Connection,
                                    tile.values.Luminances,
                                    Settings.Window,
                                    bits,
                                    Settings.coeffs,
                                    Settings.weights,
                                    out var glyph_string) && !string.IsNullOrWhiteSpace(glyph_string))
                                {
                                    glyph.Symbol = glyph_string[0];
                                    cache[tile.values] = glyph;
                                }
                                else
                                {
                                    //Not cached, calculate and add to cache
                                    SemaphoreSlim mutex = new(1, 1);
                                    List<(Glyph g, SSIMComparison c)> comparisons = new();

                                    const int THREAD_COUNT = 8;
                                    List<Thread> current_threads = new();

                                    void JoinThreads()
                                    {
                                        foreach (var thread in current_threads)
                                            thread.Join();
                                        current_threads.Clear();
                                    }

                                    foreach (var g in Glyphs)
                                    {
                                        Thread compare = new(() =>
                                        {
                                            var value = SSIMUtils.CompareSSIMs(tile.values, g.SSIM!.Value,
                                                Settings.coeffs,
                                                Settings.weights,
                                                bits);

                                            mutex.Wait();

                                            try
                                            {
                                                comparisons.Add((g, value));
                                            }
                                            finally
                                            {
                                                mutex.Release();
                                            }
                                        });
                                        compare.Start();

                                        current_threads.Add(compare);

                                        if (current_threads.Count >= THREAD_COUNT)
                                            JoinThreads();
                                    }

                                    if (current_threads.Any())
                                        JoinThreads();

                                    glyph = comparisons.MaxBy(c => c.c.Index).g;

                                    //Add glyph to dictionary and db
                                    cache[tile.values] = glyph;

                                    DBUtils.WriteMemoizedGlyph(Connection,
                                        tile.values.Luminances,
                                        Settings.Window,
                                        bits,
                                        Settings.coeffs,
                                        Settings.weights,
                                        glyph.Symbol.ToString());
                                }
                            }
                        });

                        getGlyph.Start();

                        (uint a_avg, double h_avg, double s_avg, double v_avg) = (0, 0d, 0d, 0d);

                        foreach (var pixel in tile.tile)
                        {
                            var color = pixel?.ToColor();
                            if (color != null)
                            {
                                (uint a, uint r, uint g, uint b) argb;

                                //Coalesce transparency
                                if (color.A < ushort.MaxValue && image_stack.Any())
                                {
                                    argb = InternalUtils.CoalescePixel(pixel!, image_stack);
                                }
                                else
                                {
                                    argb = (
                                       InternalUtils.ScaleUShort(color.A),
                                       InternalUtils.ScaleUShort(color.R),
                                       InternalUtils.ScaleUShort(color.G),
                                       InternalUtils.ScaleUShort(color.B));
                                }

                                (uint a, double h, double s, double v) ahsv = InternalUtils.ARGBToAHSV(argb);
                                a_avg += ahsv.a;
                                h_avg += ahsv.h;
                                s_avg += ahsv.s;
                                v_avg += ahsv.v;
                            }
                        }

                        a_avg /= (uint)tile.tile.Count();
                        h_avg /= (uint)tile.tile.Count();
                        s_avg /= (uint)tile.tile.Count();
                        v_avg /= (uint)tile.tile.Count();

                        (a_avg, uint r_avg, uint g_avg, uint b_avg) = InternalUtils.AHSVToARGB((a_avg, h_avg, s_avg, v_avg));

                        var avg_color = InternalUtils.ToUInt((a_avg, r_avg, g_avg, b_avg));

                        //avg_color /= (uint)tile.tile.Count();

                        getGlyph.Join();

                        string s = glyph.Symbol.ToString();

                        if (before_write != null)
                            s = before_write(s, avg_color);

                        sb.Append(s);

                        if (!Settings.WriteAll)
                        {
                            sw.Write(s);
                        }

                        i += Settings.Window.w;
                        if (i >= image.Width)
                        {
                            i = 0;

                            sb.AppendLine();

                            if (!Settings.WriteAll)
                            {
                                sw.WriteLine();
                                output.Flush();
                            }
                        }

                        if (after_write != null)
                            after_write(s, avg_color);
                    }

                    previous[ndx++] = sb.ToString();

                    if (Settings.WriteAll)
                    {
                        sw.Write(sb.ToString());
                        sw.Flush();
                    }
                }
                finally
                {
                    image_stack.Push(image);
                }
            }

            sw.Flush();

            repeat_count++;
        }
    }
}

/// <summary>
/// Settings to modify execution of <see cref="SSIMConverter"/>
/// </summary>
public class SSIMSettings 
{
    public static readonly (double, double, double) DEFAULT_COEFFS = (0.5d, 0.5d, 0.5d);

    public static readonly (double, double, double) DEFAULT_WEIGHTS = (0.8d, 2d, 3d);

    /// <summary>
    /// The width/height of a rectangle of pixels that should be compared to each glyph
    /// </summary>
    public (int w, int h) Window { get; set; } = (1, 1);

    /// <summary>
    /// Truncate floats in the SSIM calculation to this precision. A lower precision may
    /// decrease accuracy, but could improve performance due to dynamic programming
    /// </summary>
    public int Precision { get; set; } = 5;

    /// <summary>
    /// Modifiers to apply to each constant K, where K is a small value used to mitigate fluctuations in near-zero
    /// values
    /// </summary>
    public (double luminance, double contrast, double structure) coeffs { get; set; } = DEFAULT_COEFFS;

    /// <summary>
    /// Exponential weights to apply to each value in the SSIM index to account for different levels of
    /// importance for each value
    /// </summary>
    public (double luminance, double contrast, double structure) weights { get; set; } = DEFAULT_WEIGHTS;

    /// <summary>
    /// If specified, clamp image to these dimensions
    /// </summary>
    public (int? w, int? h) Clamp { get; set; } = (null, null);

    /// <summary>
    /// Whether ANSI escape sequences are supported
    /// </summary>
    public bool AllowANSIEscapes { get; set; } = false;

    /// <summary>
    /// The number of times to repeat an animation; -1 indicates forever
    /// </summary>
    public int Repeat { get; set; } = 0;

    /// <summary>
    /// If true, instead of writing to the stream glyph-by-glyph, will
    /// write image all at once
    /// </summary>
    public bool WriteAll { get; set; } = false;
}