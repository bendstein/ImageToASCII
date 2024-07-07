using LibI2A;
using LibI2A.Common;
using LibI2A.Converter;
using Spectre.Console;
using Spectre.Console.Cli;

namespace I2A;
public class App : AsyncCommand<AppSettings>
{
    private const string
        LAYOUT_ROOT = "Root",
        LAYOUT_LOG = "Log",
        LAYOUT_CANVAS = "Canvas",
        LAYOUT_TRAINING = "Training";

    private const int
        LIVE_UPDATE = 250,
        PREPROCESS_PRECISION = 8;

    public override async Task<int> ExecuteAsync(CommandContext context, AppSettings settings)
    {
        CancellationTokenSource cts = new();
        CancellationToken token = cts.Token;

        if (!Directory.Exists("log"))
        {
            _ = Directory.CreateDirectory("log");
        }

        bool log_console = true;

        using FileStream log_stream = new($"log/{DateTime.Now:MM-dd-yyyy-HH-mm-ss-ffff}.log", FileMode.OpenOrCreate, FileAccess.Write);
        using StreamWriter log_writer = new(log_stream);

        void Log(string message, bool is_error = false)
        {
            string full_message = $"{DateTime.Now:HH:mm:ss.ffff} {(is_error ? "ERROR | " : "INFO  | ")} {message}";

            if (log_console)
            {
                Console.WriteLine(full_message);
            }

            log_writer.WriteLine(full_message);
        }

        void WriteGlyph(string glyph, uint? color = null)
        {
            string s = glyph;

            if (color != null && !settings.NoColor)
            {
                int r = (int)(color >> 16) & 0xFF;
                int g = (int)(color >> 8) & 0xFF;
                int b = (int)color & 0xFF;
                s = Pastel.ConsoleExtensions.Pastel(s, System.Drawing.Color.FromArgb(r, g, b));
            }

            Console.Write(s);
        }

        void OnClose(object? sender, ConsoleCancelEventArgs e)
        {
            //Use default behavior if pressed more than once
            if (token.IsCancellationRequested)
            {
                e.Cancel = false;
                return;
            }

            Log("Stopping...");

            //Call for task to stop
            cts.Cancel();
            e.Cancel = true;
        }

        try
        {
            //Call for tasks to stop gracefully on ctrl + C
            Console.CancelKeyPress += OnClose;

            try
            {
                token.ThrowIfCancellationRequested();

                switch (settings.Mode)
                {
                    //Preprocess images
                    case Mode.preprocess:
                    {
                        log_console = false;

                        Log("Starting...");
                        WriteGlyph(string.Empty);

                        //Read glyphs file
                        string[] glyphs = await LoadGlyphsAsync(settings.Glyphs, token);

                        token.ThrowIfCancellationRequested();

                        if (glyphs.Length == 0)
                        {
                            throw new Exception("No glyphs were present.");
                        }

                        SSIMConverter converter = new(opts =>
                        {
                            opts.FontSize = settings.TileSize;
                            opts.FontFace = settings.FontFace;
                            opts.Subdivide = settings.SubDivide;
                            opts.ParallelCalculate = settings.Threads;
                            opts.Glyphs = glyphs;
                            opts.InvertFont = settings.InvertFont;
                        });

                        string? preprocess_dir = Path.GetDirectoryName(settings.PreprocessPath);

                        //Create directory if not exists
                        if (!string.IsNullOrWhiteSpace(preprocess_dir) && !Directory.Exists(preprocess_dir))
                        {
                            _ = Directory.CreateDirectory(preprocess_dir);
                        }

                        //Open file writer
                        using FileStream preprocess_file_stream = new(settings.PreprocessPath, FileMode.OpenOrCreate, FileAccess.Write);
                        using StreamWriter preprocess_file_writer = new(preprocess_file_stream);

                        IEnumerable<string> paths = [];

                        //Process each file
                        foreach (string? file in settings.Path.SelectMany(path =>
                        {
                            return File.Exists(path)
                                ? (new string[] { path })
                                : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) : ([]);
                        }).Distinct())
                        {
                            if (token.IsCancellationRequested)
                            {
                                break;
                            }

                            Log($"Preprocessing image {file}...");
                            using FileStream fs = new(file, FileMode.Open, FileAccess.Read);

                            int s = 0;

                            //Write each solution to preprocess file
                            foreach ((double[] intensities, (string glyph, double ssim)[] scores) solution in converter.ProcessImage(fs, WriteGlyph))
                            {
                                if (token.IsCancellationRequested)
                                {
                                    break;
                                }

                                if (s > 0 && s % 100 == 0)
                                {
                                    Log($"Preprocessing image {file}...");
                                }

                                preprocess_file_writer.WriteLine($"{string.Join(',', solution.intensities.Select(i => Utils.Round(i, PREPROCESS_PRECISION)))} ; {string.Join(" ; ", solution.scores.Select(s => $"{s.glyph} , {s.ssim}"))}");
                                s++;
                            }
                        }

                        preprocess_file_writer.Flush();
                        preprocess_file_stream.Flush();
                    }
                    break;
                    //Train on preprocessed images
                    case Mode.train:
                    {
                        token.ThrowIfCancellationRequested();

                        Log("Starting...");
                        WriteGlyph(string.Empty);

                        //Load model
                        NNConverter.Model? model = null;

                        if (!string.IsNullOrWhiteSpace(settings.Model) && File.Exists(settings.Model))
                        {
                            token.ThrowIfCancellationRequested();

                            Log($"Reading model from {settings.Model}.");

                            await using FileStream fs = new(settings.Model, FileMode.Open, FileAccess.Read);
                            await using MemoryStream ms = new();
                            await fs.CopyToAsync(ms, token);
                            byte[] bytes = ms.GetBuffer();
                            model = NNConverter.Model.FromBytes(bytes);
                        }

                        //Read glyphs file
                        string[] glyphs = model == null ? await LoadGlyphsAsync(settings.Glyphs, token) : model.Glyphs;

                        if (glyphs.Length == 0)
                        {
                            throw new Exception("No glyphs were present.");
                        }

                        //Init model if not already present
                        model ??= new NNConverter.Model(new NNConverter.ModelInitParams()
                        {
                            FeatureCount = (uint)(settings.TileSize * settings.TileSize),
                            Glyphs = glyphs,
                            HiddenLayerCount = (uint)settings.HiddenLayers,
                            HiddenLayerNeuronCount = (uint)Math.Max(1, settings.HiddenNeurons),
                            Alpha = settings.ReLUAlpha
                        });

                        SSIMConverter converter = new(opts =>
                        {
                            opts.FontSize = settings.TileSize;
                            opts.FontFace = settings.FontFace;
                            opts.Subdivide = settings.SubDivide;
                            opts.ParallelCalculate = settings.Threads;
                            opts.Glyphs = glyphs;
                            opts.InvertFont = settings.InvertFont;
                        });

                        Log("Training...");

                        token.ThrowIfCancellationRequested();

                        //Train
                        IEnumerable<NNConverter.Input> preprocessed = LoadPreprocessed(settings.PreprocessedPath, glyphs, settings.Shuffle)
                            .Where(item => item.Intensities.Length == settings.TileSize * settings.TileSize
                                && item.SSIMs.Length == model.Glyphs.Length);

                        NNConverter.TrainingSet training_set = new()
                        {
                            Input = preprocessed,
                            LearningRate = settings.LearningRate,
                            LearningDecay = settings.LearningDecay,
                            Threads = settings.Threads,
                            BatchSize = settings.BatchSize
                        };

                        //Save the model to a file
                        async Task SaveModelAsync(NNConverter.Model model, CancellationToken token)
                        {
                            if (!string.IsNullOrWhiteSpace(settings.Model))
                            {
                                string? model_dir = Path.GetDirectoryName(settings.Model);

                                if (!string.IsNullOrWhiteSpace(model_dir) && !Directory.Exists(model_dir))
                                {
                                    _ = Directory.CreateDirectory(model_dir);
                                }

                                //Log($"Writing model to {settings.Model}.");
                                await using FileStream fs = new(settings.Model, FileMode.Create, FileAccess.ReadWrite);
                                byte[] model_bytes = model.ToBytes();
                                await fs.WriteAsync(model_bytes, token);
                            }
                        }

                        token.ThrowIfCancellationRequested();

                        //Train the model
                        OneOf.OneOf<NNConverter.Model, NNConverter.TrainingError> result = await NNConverter.TrainAsync(model, training_set, Log, SaveModelAsync, token);

                        result.Switch(
                            trained => model = trained,
                            err =>
                            {
                                model = null;

                                if (err.Exception != null && err.Exception is OperationCanceledException)
                                    return;

                                throw new Exception($"Failed to train model. {err.Message}", err.Exception);
                            }
                        );

                        token.ThrowIfCancellationRequested();

                        //Save final model state
                        await SaveModelAsync(model, token);
                    }
                    break;
                    //Render the image as ASCII
                    case Mode.render:
                    default:
                    {
                        log_console = false;

                        token.ThrowIfCancellationRequested();

                        Log("Starting...");
                        WriteGlyph(string.Empty);

                        //Read glyphs file
                        string[] glyphs = await LoadGlyphsAsync(settings.Glyphs, token);

                        if (glyphs.Length == 0)
                        {
                            throw new Exception("No glyphs were present.");
                        }

                        IImageToASCIIConverter converter;

                        switch (settings.Method)
                        {
                            case Method.model:
                            {
                                //Load model
                                NNConverter.Model? model = null;

                                token.ThrowIfCancellationRequested();

                                if (!string.IsNullOrWhiteSpace(settings.Model) && File.Exists(settings.Model))
                                {
                                    Log($"Reading model from {settings.Model}.");

                                    await using FileStream fs = new(settings.Model, FileMode.Open, FileAccess.Read);
                                    await using MemoryStream ms = new();
                                    await fs.CopyToAsync(ms, token);
                                    byte[] bytes = ms.GetBuffer();
                                    model = NNConverter.Model.FromBytes(bytes);
                                }

                                if (model == null)
                                {
                                    throw new Exception($"Failed to find model at {settings.Model}");
                                }

                                converter = new NNConverter(model, opts =>
                                {
                                    opts.FontSize = settings.TileSize;
                                    opts.InvertFont = settings.InvertFont;
                                });
                            }
                            break;
                            case Method.ssim:
                            default:
                            {
                                converter = new SSIMConverter(opts =>
                                {
                                    opts.FontSize = settings.TileSize;
                                    opts.FontFace = settings.FontFace;
                                    opts.Subdivide = settings.SubDivide;
                                    opts.ParallelCalculate = settings.Threads;
                                    opts.Glyphs = glyphs;
                                    opts.InvertFont = settings.InvertFont;
                                });
                            }
                            break;
                        }

                        //Process each file
                        foreach (string? file in settings.Path.SelectMany(path =>
                        {
                            return File.Exists(path)
                                ? (new string[] { path })
                                : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) : ([]);
                        }).Distinct())
                        {
                            token.ThrowIfCancellationRequested();

                            using FileStream fs = new(file, FileMode.Open, FileAccess.ReadWrite);

                            foreach ((string? glyph, uint? color) in converter.ConvertImage(fs))
                            {
                                WriteGlyph(glyph, color);
                            }
                        }
                    }
                    break;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log(e.ToString(), true);
                throw;
            }
        }
        catch
        {
            return -1;
        }

        return 0;
    }

    private static async Task<string[]> LoadGlyphsAsync(string path, CancellationToken token = default)
    {
        //Read glyphs file
        await using FileStream fs = new(path, FileMode.Open, FileAccess.Read);
        using StreamReader sr = new(fs);

        string[] glyphs = sr.ReadToEnd().Split(Environment.NewLine)
            .Where(line => line.Length > 0)
            .Distinct()
            .ToArray();

        return glyphs;
    }

    //Read in solutions from file
    private static IEnumerable<NNConverter.Input> LoadPreprocessed(string path, string[] glyphs, bool shuffle)
    {
        DirectoryInfo? temp_path = null;

        try
        {
            IEnumerable<string> preprocessed_files = File.Exists(path) ? [path]
                : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.txt", SearchOption.AllDirectories)
                : [];

            if (shuffle)
            {
                temp_path = Directory.CreateTempSubdirectory();
                string shuffled = Path.Combine(temp_path.FullName, "preprocessed.txt");

                //Write the lines of all provided files to one file, shuffled
                using FileStream fs = new(shuffled, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                using StreamWriter sw = new(fs);

                foreach (string? line in preprocessed_files
                    .SelectMany(File.ReadAllLines)
                    .OrderBy(_ => Random.Shared.Next())
                    .ToArray())
                {
                    sw.WriteLine(line);
                }

                sw.Flush();
                fs.Flush();

                preprocessed_files = [shuffled];
            }

            foreach (string file in preprocessed_files)
            {
                using FileStream fs = new(file, FileMode.Open, FileAccess.Read);
                using StreamReader fsr = new(fs);

                string? line;
                while ((line = fsr.ReadLine()) != null)
                {
                    string[] split = line.Split(" ; ");

                    if (split.Length > 0)
                    {
                        double[] intensities = split[0].Split(',')
                            .Select(s => double.TryParse(s, out double sf) ? sf : 0)
                            .ToArray();

                        double[] ssims = new double[glyphs.Length];

                        for (int i = 1; i < split.Length; i++)
                        {
                            string[] pair = split[i].Split(" , ", 2);

                            if (pair.Length == 2)
                            {
                                string glyph = pair[0];
                                double ssim = double.TryParse(pair[1], out double f) ? f : 0;

                                for (int g = 0; g < glyphs.Length; g++)
                                {
                                    if (glyphs[g].Equals(glyph))
                                    {
                                        ssims[g] = ssim;
                                        break;
                                    }
                                }
                            }
                        }

                        yield return new()
                        {
                            Intensities = intensities,
                            SSIMs = ssims
                        };
                    }
                }
            }
        }
        finally
        {
            //Delete the temp directory
            temp_path?.Delete(true);
        }

        yield break;
    }
}
