using ImageMagick;
using LibI2A.SSIM;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;
using System.Drawing;
using System.Net;
using System.Text;
using ModelIn = LibI2A.Converter.PredictionModelSSIMConverter.ModelIn;
using ModelOut = LibI2A.Converter.PredictionModelSSIMConverter.ModelOut;

namespace LibI2A.Converter;
public class PredictionModelSSIMConverter(PredictionEngine<ModelIn, ModelOut> model,
    PredictionModelSSIMConverter.Options options) : IImageToASCIIConverter
{
    private readonly Options options = options;
    private readonly PredictionEngine<ModelIn, ModelOut> model = model;

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

            if (colors.Count > 0)
            {
                var sums = colors.AggregateOrDefault<(uint a, double h, double s, double v)>((a, b) => (a.a + b.a, a.h + b.h, a.s + b.s, a.v + b.v), (0, 0, 0, 0));
                var avgs = ((uint)long.Clamp(sums.a / colors.Count, uint.MinValue, uint.MaxValue),
                    sums.h / colors.Count,
                    sums.s / colors.Count,
                    sums.v / colors.Count);
                combined = InternalUtils.ToUInt(InternalUtils.AHSVToARGB(avgs));
            }

            //Predict the glyph that is most structurally similar to this tile
            var glyph = PredictGlyph(tile);

            yield return (glyph, combined);
            n++;
        }

        yield break;
    }

    private string PredictGlyph(PixelImage tile)
    {
        var model_in = new ModelIn([..tile.Intensities.Select(it => (float)double.Clamp(it, float.MinValue, float.MaxValue))]);
        return model.Predict(model_in).PredictedGlyph;
    }

    public static (MLContext context, ITransformer model, DataViewSchema schema) Train(ISSIMCalculator calculator, ISSIMStore store, TrainingSet set, ITransformer? existing_model = null)
    {
        MLContext context = new(set.Seed);

        //var glyph_images_inv = InternalUtils.GetGlyphImages(set.Glyphs.ToArray(), set.FontSize, set.FontFace, true)
        //    .ToDictionary(pair => pair.Key, pair => new PixelImage(pair.Value));

        var glyph_images = InternalUtils.GetGlyphImages(set.Glyphs.ToArray(), set.FontSize, set.FontFace, false)
            .ToDictionary(pair => pair.Key, pair => new PixelImage(pair.Value));

        //Create display directory
        if (!string.IsNullOrWhiteSpace(set.DisplayDir) && !Directory.Exists(set.DisplayDir))
            Directory.CreateDirectory(set.DisplayDir);

        //Create temp directory for tiles
        var tiles_dir = Directory.CreateTempSubdirectory("tiles");
        try
        {
            //Tile all inputs
            IEnumerable<ModelIn> AllTiles()
            {
                foreach (var input in set.Inputs)
                {
                    input.Position = 0;

                    using var image_collection = new MagickImageCollection(input);
                    image_collection.Coalesce();
                    var image = image_collection.First();
                    image.Format = MagickFormat.Jpg;

                    //Break images into windows
                    var pixel_image = new PixelImage(image);

                    //Width for determining where to put line breaks when printing to the console
                    int width = (int)Math.Ceiling((double)image.Width / set.FontSize);

                    var tiles = pixel_image.Tiles(set.FontSize, set.FontSize).GetEnumerator();

                    for(int n = 0; tiles.MoveNext(); n++)
                    {
                        var tile = tiles.Current;

                        //Line break in console
                        if (n > 0 && (n % width) == 0)
                            Console.WriteLine();

                        //Get glyphs that are most structurally similar to this tile
                        (string primary, Dictionary<string, float> similar) getGlyphs(Dictionary<string, PixelImage> glyphs)
                        {
                            SemaphoreSlim mutex = new(Math.Max(1, set.ParallelCalculate), Math.Max(1, set.ParallelCalculate));
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
                        {
                            yield return new ModelIn()
                            {
                                Value = [.. intensities],
                                Glyph = g.Key,
                                SSIM = g.Value
                            };
                        }
                    }

                    //Print newline between images
                    Console.WriteLine();
                }

                yield break;
            }

            //Define schema based on tile size
            var schema = SchemaDefinition.Create(typeof(ModelIn));
            schema["Features"].ColumnType = new VectorDataViewType(NumberDataViewType.Single, (int)Math.Pow(set.FontSize, 2));

            //Load tiles into training data
            var calculated_tiles = set.Preprocessed.Any()? set.Preprocessed : AllTiles().ToList();
            var training_data = context.Data.LoadFromEnumerable(calculated_tiles, schema);

            //Normalize
            var normalized = context.Transforms.NormalizeMeanVariance("Features")
                .Fit(training_data)
                .Transform(training_data);

            //Split into training and test data
            var data_split = context.Data.TrainTestSplit(normalized, 0.3d);

            //Define pipline
            var pipeline = context.Regression.Trainers.OnlineGradientDescent();

            ITransformer model;

            //Updating existing model
            if (existing_model != null
                && existing_model is RegressionPredictionTransformer<LinearRegressionModelParameters> existing_transformer
                && existing_transformer.Model is LinearRegressionModelParameters existing_params)
            {
                model = pipeline.Fit(data_split.TrainSet, existing_params);
            }
            //New model
            else
            {
                model = pipeline.Fit(data_split.TrainSet);
            }

            return (context, model, training_data.Schema);
        }
        finally
        {
            //Delete temp dir
            tiles_dir.Delete(true);
        }
    }

    public class Options
    {
        public int FontSize { get; set; } = 12;

        public string FontFace { get; set; } = string.Empty;

        public bool InvertFont { get; set; } = false;
    }

    public class TrainingSet : IDisposable
    {
        public IEnumerable<Stream> Inputs { get; set; } = [];

        public IEnumerable<string> Glyphs { get; set; } = [];

        public int FontSize { get; set; } = 12;

        public string FontFace { get; set; } = string.Empty;

        public int? Seed { get; set; }

        public string DisplayDir { get; set; } = string.Empty;

        public int ParallelCalculate { get; set; } = 1;

        public string TileDir { get; set; } = string.Empty;

        public IEnumerable<ModelIn> Preprocessed = [];

        public void Dispose()
        {
            foreach (var stream in Inputs)
                stream.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    public class ModelIn(float[] value)
    {
        [VectorType]
        [ColumnName("Features")]
        public float[] Value { get; set; } = value;

        public string Glyph { get; set; } = string.Empty;

        [ColumnName("Label")]
        public float GlyphASCII
        {
            get => string.IsNullOrWhiteSpace(Glyph)? 0 : Glyph[0];
            set => Glyph = ((char)(byte)float.Clamp(value, byte.MinValue, byte.MaxValue)).ToString();
        }

        [ColumnName("Weight")]
        public float SSIM { get; set; } = 0f;

        public ModelIn() : this([]) { }
    }
    
    public class ModelOut(string glyph)
    {
        public string PredictedGlyph { get; set; } = glyph;

        [ColumnName("Label")]
        public int PredictedGlyphASCII
        {
            get => string.IsNullOrWhiteSpace(PredictedGlyph) ? 0 : PredictedGlyph[0];
            set => PredictedGlyph = ((char)(byte)float.Clamp(value, byte.MinValue, byte.MaxValue)).ToString();
        }

        public ModelOut() : this(string.Empty) { }
    }
}
