using ImageMagick;
using LibI2A.SSIM;
using static LibI2A.Converter.PredictionModelSSIMConverter;

namespace LibI2A.Converter;
public class SSIMConverter : IImageToASCIIConverter
{
    private readonly SSIMCalculator calculator;

    private readonly ISSIMStore store;

    private readonly Dictionary<string, PixelImage> glyph_images;

    private readonly Options options;

    public SSIMConverter(ISSIMStore store, Action<Options>? configure = null)
    {
        this.store = store;
        Options options = new();
        configure?.Invoke(options);
        this.options = options;
        glyph_images = InternalUtils.GetGlyphImages(options.Glyphs.ToArray(), options.FontSize, options.FontFace, options.InvertFont)
            .ToDictionary(pair => pair.Key, pair => new PixelImage(pair.Value));
        calculator = new SSIMCalculator(store, this.options.SSIMCalculatorOptions);
    }

    public SSIMConverter(ISSIMStore store, Options options)
    {
        this.store = store;
        this.options = options;
        glyph_images = InternalUtils.GetGlyphImages(options.Glyphs.ToArray(), options.FontSize, options.FontFace, options.InvertFont)
            .ToDictionary(pair => pair.Key, pair => new PixelImage(pair.Value));
        calculator = new SSIMCalculator(store, this.options.SSIMCalculatorOptions);
    }

    public IEnumerable<(string glyph, uint? color)> ConvertImage(Stream input)
    {
        using var image_collection = new MagickImageCollection(input);
        image_collection.Coalesce();
        var image = image_collection.First();

        //Break images into windows
        var pixel_image = new PixelImage(image);

        int width = (int)Math.Ceiling((double)image.Width / options.FontSize);
        int height = (int)Math.Ceiling((double)image.Height / options.FontSize);

        int n = 0;

        foreach (var tile in pixel_image.Tiles(options.FontSize, options.FontSize))
        {
            if (n > 0 && (n % width) == 0)
                yield return (Environment.NewLine, null);

            //Get average color of tile
            var colors = tile.Pixels
                .Where(color => color != null)
                .Select(color => InternalUtils.ARGBToAHSV((
                    InternalUtils.ScaleUShort(color!.A),
                    InternalUtils.ScaleUShort(color.R),
                    InternalUtils.ScaleUShort(color.G),
                    InternalUtils.ScaleUShort(color.B))))
                .ToList();

            uint combined = 0;

            if(colors.Count > 0)
            {
                var sums = colors.AggregateOrDefault<(uint a, double h, double s, double v)>((a, b) => (a.a + b.a, a.h + b.h, a.s + b.s, a.v + b.v), (0, 0, 0, 0));
                var avgs = ((uint)long.Clamp(sums.a / colors.Count, uint.MinValue, uint.MaxValue),
                    sums.h / colors.Count,
                    sums.s / colors.Count,
                    sums.v / colors.Count);
                combined = InternalUtils.ToUInt(InternalUtils.AHSVToARGB(avgs));
            }

            //Get glyph that is most structurally similar to this tile
            var glyph = store.GetOrCalculateAndStoreSoln([..tile.Intensities],
                () =>
                {
                    SemaphoreSlim mutex = new(Math.Max(1, options.ParallelCalculate), Math.Max(1, options.ParallelCalculate));
                    (string key, double score) max = (string.Empty, double.MinValue); 
                    var lk = new { };

                    List<Thread> threads = glyph_images.Select(img => new Thread(() =>
                    {
                        try
                        {
                            var score = calculator.Calculate(tile, img.Value);

                            //If better than current max, record
                            lock(lk)
                                if (score > max.score)
                                    max = (img.Key, score);
                        }
                        finally
                        {
                            mutex.Release();
                        }
                    })).ToList();

                    //Calculate in parallel
                    foreach(var thread in threads)
                    {
                        mutex.Wait();
                        thread.Start();
                    }

                    //Wait for all threads to complete
                    foreach(var thread in threads)
                        thread.Join();

                    //Return maximum
                    return max.key;
                });

            yield return (glyph, combined);
            n++;
        }

        yield break;
    }

    public IEnumerable<(float[] intensities, float ssim, string glyph)> PreprocessImage(Stream input)
    {
        using var image_collection = new MagickImageCollection(input);
        image_collection.Coalesce();
        var image = image_collection.First();

        //Break images into windows
        var pixel_image = new PixelImage(image);

        int width = (int)Math.Ceiling((double)image.Width / options.FontSize);
        int height = (int)Math.Ceiling((double)image.Height / options.FontSize);

        var tiles = pixel_image.Tiles(options.FontSize, options.FontSize).GetEnumerator();

        for (int n = 0; tiles.MoveNext(); n++)
        {
            var tile = tiles.Current;

            //Line break in console
            if (n > 0 && (n % width) == 0)
                Console.WriteLine();

            //Get glyphs that are most structurally similar to this tile
            (string primary, Dictionary<string, float> similar) getGlyphs(Dictionary<string, PixelImage> glyphs)
            {
                SemaphoreSlim mutex = new(Math.Max(1, options.ParallelCalculate), Math.Max(1, options.ParallelCalculate));
                Dictionary<string, float> scores = [];
                (string key, float value) max = (string.Empty, float.MinValue);

                //Find the glyph(s) that are most similar to this tile
                List<Thread> threads = glyphs.Select(img => new Thread(() =>
                {
                    try
                    {
                        var score = (float)double.Clamp(calculator.Calculate(tile, img.Value), float.MinValue, float.MaxValue);

                        lock (scores)
                        {
                            //If better than current max, record as max
                            if (score > max.value)
                                max = (img.Key, score);

                            //Record score
                            scores[img.Key] = score;
                        }
                    }
                    finally
                    {
                        mutex.Release();
                    }
                })).ToList();

                //Calculate in parallel
                foreach (var thread in threads)
                {
                    mutex.Wait();
                    thread.Start();
                }

                //Wait for all threads to complete
                foreach (var thread in threads)
                    thread.Join();

                //Return all glyphs within 5% of the maximum
                const float ERR = 0.05f;

                var matching = scores.Where(pair => (Math.Abs(pair.Value - max.value) / max.value) < ERR).ToDictionary();

                //Return maximum
                return (max.key, matching);
            }

            (var glyph, var glyphs) = getGlyphs(glyph_images);
            //(var inv_glyph, var inv_glyphs) = getGlyphs(glyph_images_inv);

            var intensities = tile.Intensities.Select(it => (float)double.Clamp(it, float.MinValue, float.MaxValue));

            //Print random matching glyph to the console
            if (glyphs.Count > 0)
                Console.Write(glyphs.Keys.OrderBy(k => Random.Shared.Next()).First());
            else
                Console.Write(' ');

            foreach (var g in glyphs)
                yield return (intensities.ToArray(), g.Value, g.Key);
        }

        Console.WriteLine();
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