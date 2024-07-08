using ImageMagick;
using LibI2A.Common;
using Pastel;

namespace LibI2A.Converter;
public class SSIMConverter : IImageToASCIIConverter
{
    private readonly SSIMCalculator calculator;

    private readonly Dictionary<string, PixelImage> glyph_images;

    private readonly Options options;

    public SSIMConverter(Action<Options>? configure = null)
    {
        Options options = new();
        configure?.Invoke(options);
        this.options = options;
        glyph_images = Utils.GetGlyphImages(options.Glyphs.ToArray(), options.FontSize, options.FontFace, options.InvertFont)
            .ToDictionary(pair => pair.Key, pair => new PixelImage(pair.Value));
        calculator = new SSIMCalculator(this.options.SSIMCalculatorOptions);
    }

    public SSIMConverter(Options options)
    {
        this.options = options;
        glyph_images = Utils.GetGlyphImages(options.Glyphs.ToArray(), options.FontSize, options.FontFace, options.InvertFont)
            .ToDictionary(pair => pair.Key, pair => new PixelImage(pair.Value));
        calculator = new SSIMCalculator(this.options.SSIMCalculatorOptions);
    }

    public IEnumerable<(string glyph, uint? color)> ConvertImage(Stream input)
    {
        using MagickImageCollection image_collection = new(input);
        image_collection.Coalesce();
        IMagickImage<ushort> image = image_collection.First();

        //Break images into windows
        PixelImage pixel_image = new(image);

        int width = (int)Math.Ceiling((double)image.Width / options.FontSize);
        int height = (int)Math.Ceiling((double)image.Height / options.FontSize);

        int n = 0;

        foreach (PixelImage tile in pixel_image.Tiles(options.FontSize, options.FontSize))
        {
            if (n > 0 && (n % width) == 0)
            {
                yield return (Environment.NewLine, null);
            }

            //Get average color of tile
            List<(uint a, double h, double s, double v)> colors = tile.Pixels
                .Where(color => color != null)
                .Select(color => Utils.ARGBToAHSV((
                    Utils.ScaleUShort(color!.A),
                    Utils.ScaleUShort(color.R),
                    Utils.ScaleUShort(color.G),
                    Utils.ScaleUShort(color.B))))
                .ToList();

            uint combined = 0;

            if (colors.Count > 0)
            {
                (uint a, double h, double s, double v) sums = colors.AggregateOrDefault<(uint a, double h, double s, double v)>((a, b) => (a.a + b.a, a.h + b.h, a.s + b.s, a.v + b.v), (0, 0, 0, 0));
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

            List<Thread> threads = glyph_images.Select(img => new Thread(() =>
            {
                try
                {
                    double score = calculator.Calculate(tile, img.Value);

                    //If better than current max, record
                    lock (lk)
                    {
                        if (score > max.score)
                        {
                            max = (img.Key, score);
                        }
                    }
                }
                finally
                {
                    _ = mutex.Release();
                }
            })).ToList();

            //Calculate in parallel
            foreach (Thread thread in threads)
            {
                mutex.Wait();
                thread.Start();
            }

            //Wait for all threads to complete
            foreach (Thread thread in threads)
            {
                thread.Join();
            }

            //Return maximum
            string glyph = max.key;

            yield return (glyph, combined);
            n++;
        }

        yield break;
    }

    public IEnumerable<(double[] intensities, (string glyph, double ssim)[] scores)> ProcessImage(Stream input, Action<string, uint?>? writer = null)
    {
        void WriteLine()
        {
            if (writer == null)
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
            if (writer == null)
            {
                string cs = s;

                if (color.HasValue)
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

        //Break images into windows
        PixelImage pixel_image = new(image);

        int width = (int)Math.Ceiling((double)image.Width / options.FontSize);
        int height = (int)Math.Ceiling((double)image.Height / options.FontSize);

        IEnumerator<PixelImage> tiles = pixel_image.Tiles(options.FontSize, options.FontSize).GetEnumerator();

        for (int n = 0; tiles.MoveNext(); n++)
        {
            PixelImage tile = tiles.Current;

            //Line break in console
            if (n > 0 && (n % width) == 0)
            {
                WriteLine();
            }

            //Get average color of tile
            List<(uint a, double h, double s, double v)> colors = tile.Pixels
                .Where(color => color != null)
                .Select(color => Utils.ARGBToAHSV((
                    Utils.ScaleUShort(color!.A),
                    Utils.ScaleUShort(color.R),
                    Utils.ScaleUShort(color.G),
                    Utils.ScaleUShort(color.B))))
                .ToList();

            uint combined = 0;

            if (colors.Count > 0)
            {
                (uint a, double h, double s, double v) sums = colors.AggregateOrDefault<(uint a, double h, double s, double v)>((a, b) => (a.a + b.a, a.h + b.h, a.s + b.s, a.v + b.v), (0, 0, 0, 0));
                (uint, double, double, double) avgs = ((uint)long.Clamp(sums.a / colors.Count, uint.MinValue, uint.MaxValue),
                    sums.h / colors.Count,
                    sums.s / colors.Count,
                    sums.v / colors.Count);
                combined = Utils.ToUInt(Utils.AHSVToARGB(avgs));
            }

            //Get all glyphs and values
            (string primary, Dictionary<string, double> similar) getGlyphs(Dictionary<string, PixelImage> glyphs)
            {
                SemaphoreSlim mutex = new(Math.Max(1, options.ParallelCalculate), Math.Max(1, options.ParallelCalculate));
                Dictionary<string, double> scores = [];
                (string key, double value) max = (string.Empty, double.MinValue);

                //Find the glyph(s) that are most similar to this tile
                List<Thread> threads = glyphs.Select(img => new Thread(() =>
                {
                    try
                    {
                        double score = calculator.Calculate(tile, img.Value);

                        lock (scores)
                        {
                            //If better than current max, record as max
                            if (score > max.value)
                            {
                                max = (img.Key, score);
                            }

                            //Record score
                            scores[img.Key] = score;
                        }
                    }
                    finally
                    {
                        _ = mutex.Release();
                    }
                })).ToList();

                //Calculate in parallel
                foreach (Thread thread in threads)
                {
                    mutex.Wait();
                    thread.Start();
                }

                //Wait for all threads to complete
                foreach (Thread thread in threads)
                {
                    thread.Join();
                }

                //Return maximum
                return (max.key, scores);
            }

            (string? glyph, Dictionary<string, double>? glyphs) = getGlyphs(glyph_images);
            //(var inv_glyph, var inv_glyphs) = getGlyphs(glyph_images_inv);

            //Print best match
            Write(glyph, combined);

            double[] intensities = [.. tile.GetIntensities()];

            yield return (intensities, glyphs.Select(g => (g.Key, g.Value)).ToArray());
        }

        WriteLine();
        yield break;
    }

    public class Options
    {
        public int Subdivide { get; set; } = 1;

        public (double K1, double K2) Constants = (0.01d, 0.03d);

        public double GaussianStdDev { get; set; } = 1.5d;

        public int FontSize { get; set; } = 12;

        public string FontFace { get; set; } = string.Empty;

        public bool InvertFont { get; set; } = false;

        public int ParallelCalculate { get; set; } = 1;

        public IEnumerable<string> Glyphs { get; set; } = [];

        public SSIMCalculator.Options SSIMCalculatorOptions
            => new()
            {
                Subdivide = Subdivide,
                Constants = Constants,
                GaussianStdDev = GaussianStdDev
            };
    }
}