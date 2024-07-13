using ImageMagick;
using LibI2A.Calculator;
using LibI2A.Common;
using Pastel;

namespace LibI2A.Converter;
public class SSIMConverter : IImageToASCIIConverter
{
    private readonly ISSIMCalculator calculator;

    private readonly Options options;

    public SSIMConverter(ISSIMCalculator calculator, Action<Options>? configure = null)
    {
        this.calculator = calculator;
        Options options = new();
        configure?.Invoke(options);
        this.options = options;
    }

    public SSIMConverter(ISSIMCalculator calculator, Options options)
    {
        this.calculator = calculator;
        this.options = options;
    }

    public IEnumerable<(string glyph, uint? color)> ConvertImage(Stream input)
    {
        using MagickImageCollection image_collection = new(input);
        image_collection.Coalesce();
        IMagickImage<ushort> image = image_collection.First();

        //Break images into windows
        PixelImage pixel_image = new(image);

        var width = (int)Math.Ceiling((double)image.Width / options.FontSize);
        var height = (int)Math.Ceiling((double)image.Height / options.FontSize);

        var n = 0;

        foreach(PixelImage tile in pixel_image.Tiles(options.FontSize, options.FontSize))
        {
            if(n > 0 && (n % width) == 0)
            {
                yield return (Environment.NewLine, null);
            }

            //Get average color of tile
            var colors = tile.Pixels
                .Where(color => color != null)
                .Select(color => Utils.ARGBToAHSV((
                    Utils.ScaleUShort(color!.A),
                    Utils.ScaleUShort(color.R),
                    Utils.ScaleUShort(color.G),
                    Utils.ScaleUShort(color.B))))
                .ToList();

            uint combined = 0;

            if(colors.Count > 0)
            {
                (uint a, double h, double s, double v) sums = colors.AggregateOrDefault<(uint a, double h, double s, double v)>((a, b) =>
                    (a.a + b.a,
                    a.h + b.h,
                    a.s + b.s,
                    a.v + b.v), (0, 0, 0, 0));
                (uint, double, double, double) avgs = ((uint)long.Clamp(sums.a / colors.Count, uint.MinValue, uint.MaxValue),
                    sums.h / colors.Count,
                    sums.s / colors.Count,
                    sums.v / colors.Count);
                combined = Utils.ToUInt(Utils.AHSVToARGB(avgs));
            }

            //Get glyph that is most structurally similar to this tile
            SemaphoreSlim mutex = new(Math.Max(1, options.ParallelCalculate), Math.Max(1, options.ParallelCalculate));
            (string key, double score) max = (string.Empty, double.MinValue);
            var lk = new { };

            var threads = options.GlyphClasses.Keys.Select(glyph => new Thread(() =>
            {
                try
                {
                    var score = calculator.CalculateSSIM(tile, glyph);

                    //If better than current max, record
                    lock(lk)
                    {
                        if(score > max.score)
                        {
                            max = (glyph, score);
                        }
                    }
                }
                finally
                {
                    _ = mutex.Release();
                }
            })).ToList();

            //Calculate in parallel
            foreach(Thread thread in threads)
            {
                mutex.Wait();
                thread.Start();
            }

            //Wait for all threads to complete
            foreach(Thread thread in threads)
            {
                thread.Join();
            }

            //Return maximum
            var glyph = max.key;

            yield return (glyph, combined);
            n++;
        }

        yield break;
    }

    public IEnumerable<(double[] intensities, (string glyph, double ssim)[] scores)> ProcessImage(Stream input, Action<string, uint?>? writer = null)
    {
        void WriteLine()
        {
            if(writer == null)
            {
                Console.WriteLine();
            }
            else
            {
                writer.Invoke(Environment.NewLine, null);
            }
        }

        void Write(string s, uint? color = null)
        {
            if(writer == null)
            {
                var cs = s;

                if(color.HasValue)
                {
                    System.Drawing.Color? c = System.Drawing.Color.FromArgb(
                        (int)(color >> 24) & 0xFF,
                        (int)(color >> 16) & 0xFF,
                        (int)(color >> 8) & 0xFF,
                        (int)color & 0xFF);

                    cs = cs.Pastel(c.Value);
                }

                Console.Write(cs);
            }
            else
            {
                writer.Invoke(s, color);
            }
        }

        using MagickImageCollection image_collection = new(input);
        image_collection.Coalesce();
        IMagickImage<ushort> image = image_collection.First();

        foreach(var augmented_image in AugmentImage(image))
        {
            //Break images into windows
            PixelImage pixel_image = new(augmented_image);

            var width = (int)Math.Ceiling((double)augmented_image.Width / options.FontSize);
            var height = (int)Math.Ceiling((double)augmented_image.Height / options.FontSize);

            IEnumerator<PixelImage> tiles = pixel_image.Tiles(options.FontSize, options.FontSize).GetEnumerator();

            for(var n = 0; tiles.MoveNext(); n++)
            {
                PixelImage tile = tiles.Current;

                //Line break in console
                if(n > 0 && (n % width) == 0)
                {
                    WriteLine();
                }

                //Get average color of tile
                var colors = tile.Pixels
                    .Where(color => color != null)
                    .Select(color => Utils.ARGBToAHSV((
                        Utils.ScaleUShort(color!.A),
                        Utils.ScaleUShort(color.R),
                        Utils.ScaleUShort(color.G),
                        Utils.ScaleUShort(color.B))))
                    .ToList();

                uint combined = 0;

                if(colors.Count > 0)
                {
                    (uint a, double h, double s, double v) sums = colors.AggregateOrDefault<(uint a, double h, double s, double v)>((a, b) => (a.a + b.a, a.h + b.h, a.s + b.s, a.v + b.v), (0, 0, 0, 0));
                    (uint, double, double, double) avgs = ((uint)long.Clamp(sums.a / colors.Count, uint.MinValue, uint.MaxValue),
                        sums.h / colors.Count,
                        sums.s / colors.Count,
                        sums.v / colors.Count);
                    combined = Utils.ToUInt(Utils.AHSVToARGB(avgs));
                }

                //Get all glyphs and values
                (string primary, Dictionary<string, double> similar) getGlyphs(IEnumerable<string> glyphs)
                {
                    SemaphoreSlim mutex = new(Math.Max(1, options.ParallelCalculate), Math.Max(1, options.ParallelCalculate));
                    Dictionary<string, double> scores = [];
                    (string key, double value) max = (string.Empty, double.MinValue);

                    //Find the glyph(s) that are most similar to this tile
                    var threads = glyphs.Select(glyph => new Thread(() =>
                    {
                        try
                        {
                            var score = calculator.CalculateSSIM(tile, glyph);

                            lock(scores)
                            {
                                //If better than current max, record as max
                                if(score > max.value)
                                {
                                    max = (glyph, score);
                                }

                                //Record score
                                scores[glyph] = score;
                            }
                        }
                        finally
                        {
                            _ = mutex.Release();
                        }
                    })).ToList();

                    //Calculate in parallel
                    foreach(Thread thread in threads)
                    {
                        mutex.Wait();
                        thread.Start();
                    }

                    //Wait for all threads to complete
                    foreach(Thread thread in threads)
                    {
                        thread.Join();
                    }

                    //Return maximum
                    return (max.key, scores);
                }

                (var glyph, Dictionary<string, double>? glyphs) = getGlyphs(options.GlyphClasses.Keys);

                //Print best match
                Write(glyph, combined);

                double[] intensities = [.. tile.GetIntensities()];

                yield return (intensities, glyphs.Select(g => (g.Key, g.Value)).ToArray());
            }

            WriteLine();
        }

        yield break;
    }

    private static IEnumerable<IMagickImage<ushort>> AugmentImage(IMagickImage<ushort> image)
    {
        for(int angle = 0; angle < 180; angle += 30)
        {
            for(double scale = 0.5d; scale <= 1.5d; scale += 0.5d)
            {
                for(int noise = 0; noise < 2; noise++)
                {
                    for(int flip = 0; flip < 2; flip++)
                    {
                        for(int flop = 0; flop < 2; flop++)
                        {
                            for(int negate = 0; negate < 2; negate++)
                            {
                                using var clone = image.Clone();

                                //Rotate image
                                if(angle > 0)
                                    clone.Rotate(angle);

                                //Scale image
                                if(scale != 1d)
                                    clone.Scale(new Percentage(scale * 100d));

                                //Add gaussian noise
                                if(noise == 1)
                                    clone.AddNoise(NoiseType.Gaussian);

                                //Flip vertically
                                if(flip == 1)
                                    clone.Flip();

                                //Flip horizontally
                                if(flop == 1)
                                    clone.Flop();

                                //Invert greyscale channel
                                if(negate == 1)
                                    clone.Negate(Channels.Gray);

                                yield return clone;
                            }
                        }
                    }
                }
            }
        }

        yield break;
    }

    public class Options
    {
        public int FontSize { get; set; } = 12;

        public int ParallelCalculate { get; set; } = 1;

        public Dictionary<string, string[]> GlyphClasses { get; set; } = [];
    }
}