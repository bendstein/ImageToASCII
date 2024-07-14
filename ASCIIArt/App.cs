using LibI2A;
using LibI2A.Calculator;
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

    private const double
        CREATE_GLYPH_CLASSES_CONFIDENCE = 0.90d;

    public override async Task<int> ExecuteAsync(CommandContext context, AppSettings settings)
    {
        CancellationTokenSource cts = new();
        CancellationToken token = cts.Token;

        var log_console = true;

        void Log(string message, bool is_error = false)
        {
            var full_message = $"{DateTime.Now:HH:mm:ss.ffff} {(is_error ? "ERROR | " : "INFO  | ")} {message}";

            if(log_console)
                Console.WriteLine(full_message);
        }

        void WriteGlyph(string glyph, uint? color = null)
        {
            var s = glyph;

            if(color != null && !settings.NoColor)
            {
                var r = (int)(color >> 16) & 0xFF;
                var g = (int)(color >> 8) & 0xFF;
                var b = (int)color & 0xFF;
                s = Pastel.ConsoleExtensions.Pastel(s, System.Drawing.Color.FromArgb(r, g, b));
            }

            Console.Write(s);
        }

        void OnClose(object? sender, ConsoleCancelEventArgs e)
        {
            //Use default behavior if pressed more than once
            if(token.IsCancellationRequested)
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

                if(settings.CreateGlyphs)
                {
                    Log($"Creating {settings.Glyphs}.");

                    List<char> chars = [];
                    for(byte i = 0; i < byte.MaxValue; i++)
                    {
                        var c = (char)i;

                        if(!char.IsWhiteSpace(c) && !char.IsControl(c) && char.IsAscii(c))
                        {
                            chars.Add(c);
                        }
                    }

                    _ = await CreateGlyphsAsync(chars.Select(c => c.ToString()), settings.Glyphs, settings, token);

                    Log($"Successfully created {settings.Glyphs}.");
                }

                switch(settings.Mode)
                {
                    //Preprocess images
                    case Mode.preprocess:
                    {
                        log_console = false;

                        Log("Starting...");
                        WriteGlyph(string.Empty);

                        //Read glyphs file
                        Dictionary<string, string[]> glyphs = await LoadGlyphsAsync(settings.Glyphs, token);

                        token.ThrowIfCancellationRequested();

                        if(glyphs.Count == 0)
                        {
                            throw new Exception("No glyphs were present.");
                        }

                        var glyph_images = Utils.GetGlyphImages([.. glyphs.Keys], settings.TileSize, settings.FontFace, settings.InvertFont)
                                    .ToDictionary(pair => pair.Key, pair => new PixelImage(pair.Value));

                        var calculator = new SSIMCalculator(glyph_images, configure =>
                        {
                            configure.Subdivide = settings.SubDivide;
                            configure.GaussianStdDev = 1.5d;
                        });

                        SSIMConverter converter = new(calculator, opts =>
                        {
                            opts.FontSize = settings.TileSize;
                            opts.ParallelCalculate = settings.Threads;
                            opts.GlyphClasses = glyphs;
                        });

                        var preprocess_dir = Path.GetDirectoryName(settings.PreprocessPath);

                        //Create directory if not exists
                        if(!string.IsNullOrWhiteSpace(preprocess_dir) && !Directory.Exists(preprocess_dir))
                        {
                            _ = Directory.CreateDirectory(preprocess_dir);
                        }

                        //Open file writer
                        using FileStream preprocess_file_stream = new(settings.PreprocessPath, FileMode.OpenOrCreate, FileAccess.Write);
                        using StreamWriter preprocess_file_writer = new(preprocess_file_stream);

                        //Process each file, and write their SSIM data to a file
                        foreach((double[] intensities, (string glyph, double ssim)[] scores) solution in ProcessImages(converter, settings.Path, WriteGlyph))
                        {
                            if(token.IsCancellationRequested)
                            {
                                break;
                            }

                            preprocess_file_writer.WriteLine($"{string.Join(',', solution.intensities.Select(i => Utils.Round(i, PREPROCESS_PRECISION)))} ; {string.Join(" ; ", solution.scores.Select(s => $"{s.glyph} , {s.ssim}"))}");
                        }

                        preprocess_file_writer.Flush();
                        preprocess_file_stream.Flush();
                    }
                    break;
                    //Train on processed images
                    case Mode.train:
                    {
                        token.ThrowIfCancellationRequested();

                        Log("Starting...");
                        WriteGlyph(string.Empty);

                        //Load model
                        NNConverter.Model? model = null;

                        if(!string.IsNullOrWhiteSpace(settings.Model) && File.Exists(settings.Model))
                        {
                            token.ThrowIfCancellationRequested();

                            Log($"Reading model from {settings.Model}.");

                            await using FileStream fs = new(settings.Model, FileMode.Open, FileAccess.Read);
                            await using MemoryStream ms = new();
                            await fs.CopyToAsync(ms, token);
                            var bytes = ms.GetBuffer();
                            model = NNConverter.Model.FromBytes(bytes);
                        }

                        //Read glyphs file
                        Dictionary<string, string[]> glyphs = await LoadGlyphsAsync(settings.Glyphs, token);

                        if(glyphs.Count == 0)
                        {
                            throw new Exception("No glyphs were present.");
                        }

                        //Init model if not already present
                        model ??= new NNConverter.Model(new NNConverter.ModelInitParams()
                        {
                            FeatureCount = (uint)(settings.TileSize * settings.TileSize),
                            Glyphs = glyphs.Keys.ToArray(),
                            HiddenLayerCount = (uint)settings.HiddenLayers,
                            HiddenLayerNeuronCount = (uint)Math.Max(1, settings.HiddenNeurons),
                            Alpha = settings.ReLUAlpha
                        });

                        Log("Training...");

                        token.ThrowIfCancellationRequested();

                        //Get training set
                        IEnumerable<NNConverter.Input> training_input = [];

                        if(string.IsNullOrWhiteSpace(settings.PreprocessPath))
                        {
                            var glyph_images = Utils.GetGlyphImages([.. glyphs.Keys], settings.TileSize, settings.FontFace, settings.InvertFont)
                                .ToDictionary(pair => pair.Key, pair => new PixelImage(pair.Value));

                            var calculator = new SSIMCalculator(glyph_images, configure =>
                            {
                                configure.Subdivide = settings.SubDivide;
                                configure.GaussianStdDev = 1.5d;
                            });

                            //Train on data while it's converted
                            SSIMConverter converter = new(calculator, opts =>
                            {
                                opts.FontSize = settings.TileSize;
                                opts.ParallelCalculate = settings.Threads;
                                opts.GlyphClasses = glyphs;
                            });

                            //Process each file, and yield the training data
                            IEnumerable<NNConverter.Input> GetImageData()
                            {
                                foreach((double[] intensities, (string glyph, double ssim)[] scores) solution in ProcessImages(converter, settings.Path, (_, _) => { }))
                                {
                                    if(token.IsCancellationRequested)
                                    {
                                        break;
                                    }

                                    yield return new NNConverter.Input()
                                    {
                                        Intensities = solution.intensities,
                                        SSIMs = solution.scores.Select(s => s.ssim).ToArray()
                                    };
                                }

                                yield break;
                            }

                            training_input = GetImageData();
                        }
                        else
                        {
                            //Load preprocessed data
                            training_input = LoadPreprocessed(settings.PreprocessPath, [.. glyphs.Keys], settings.Shuffle);
                        }

                        training_input = training_input.Where((item, ndx) =>
                        {
                            if(item.Intensities.Length == settings.TileSize * settings.TileSize)
                            {
                                if(item.SSIMs.Length == model.Glyphs.Length)
                                {
                                    return true;
                                }
                                else
                                {
                                    Log($"Skipping item {ndx}, as SSIMs length ({item.SSIMs.Length}) is incorrect (should be {model.Glyphs.Length}).", true);
                                }
                            }
                            else
                            {
                                Log($"Skipping item {ndx}, as intensities length ({item.Intensities.Length}) is incorrect (should be {settings.TileSize * settings.TileSize}).", true);
                            }

                            return false;
                        });

                        if(!training_input.Any())
                        {
                            throw new Exception("Training set is empty!");
                        }

                        NNConverter.TrainingSet training_set = new()
                        {
                            Input = training_input,
                            LearningRate = settings.LearningRate,
                            Threads = settings.Threads,
                            BatchSize = settings.BatchSize,
                            Lambda = settings.Lambda,
                            AdamParams = settings.AdamParams
                        };

                        //Save the model to a file
                        async Task SaveModelAsync(NNConverter.Model model, CancellationToken token)
                        {
                            token.ThrowIfCancellationRequested();

                            if(!string.IsNullOrWhiteSpace(settings.Model))
                            {
                                var model_dir = Path.GetDirectoryName(settings.Model);

                                if(!string.IsNullOrWhiteSpace(model_dir) && !Directory.Exists(model_dir))
                                {
                                    _ = Directory.CreateDirectory(model_dir);
                                }

                                token.ThrowIfCancellationRequested();

                                //Don't cancel until after write is complete
                                await using FileStream fs = new(settings.Model, FileMode.Create, FileAccess.ReadWrite);
                                var model_bytes = model.ToBytes();
                                await fs.WriteAsync(model_bytes, CancellationToken.None);

                                token.ThrowIfCancellationRequested();
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

                                if(err.Exception is not null and OperationCanceledException)
                                {
                                    return;
                                }

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
                        Dictionary<string, string[]> glyphs = await LoadGlyphsAsync(settings.Glyphs, token);

                        if(glyphs.Count == 0)
                        {
                            throw new Exception("No glyphs were present.");
                        }

                        IImageToASCIIConverter converter;

                        switch(settings.Method)
                        {
                            case Method.model:
                            {
                                //Load model
                                NNConverter.Model? model = null;

                                token.ThrowIfCancellationRequested();

                                if(!string.IsNullOrWhiteSpace(settings.Model) && File.Exists(settings.Model))
                                {
                                    Log($"Reading model from {settings.Model}.");

                                    await using FileStream fs = new(settings.Model, FileMode.Open, FileAccess.Read);
                                    await using MemoryStream ms = new();
                                    await fs.CopyToAsync(ms, token);
                                    var bytes = ms.GetBuffer();
                                    model = NNConverter.Model.FromBytes(bytes);
                                }

                                if(model == null)
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
                                var glyph_images = Utils.GetGlyphImages([.. glyphs.Keys], settings.TileSize, settings.FontFace, settings.InvertFont)
                                    .ToDictionary(pair => pair.Key, pair => new PixelImage(pair.Value));

                                var calculator = new SSIMCalculator(glyph_images, configure =>
                                {
                                    configure.Subdivide = settings.SubDivide;
                                    configure.GaussianStdDev = 1.5d;
                                });

                                converter = new SSIMConverter(calculator, opts =>
                                {
                                    opts.FontSize = settings.TileSize;
                                    opts.ParallelCalculate = settings.Threads;
                                    opts.GlyphClasses = glyphs;
                                });
                            }
                            break;
                        }

                        //Process and render each image
                        foreach((var glyph, var color) in ConvertImages(converter, settings.Path))
                        {
                            WriteGlyph(glyph, color);
                        }
                    }
                    break;
                }
            }
            catch(OperationCanceledException)
            {
                return -1;
            }
            catch(Exception e) when(e is not OperationCanceledException)
            {
                log_console = true;
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

    private static async Task<Dictionary<string, string[]>> LoadGlyphsAsync(string path, CancellationToken token = default)
    {
        //Read glyphs file
        await using FileStream fs = new(path, FileMode.Open, FileAccess.Read);
        using StreamReader sr = new(fs);

        token.ThrowIfCancellationRequested();

        var classes = (await sr.ReadToEndAsync(token))
            .Split('\n')
            .Where(line => line.Length > 0)
            .Select(line =>
            {
                var split = line.Split(" ;; ", 2);

                var head = split.Length > 0 ? split[0] : string.Empty;
                var tail = split.Length > 1 ? split[1] : string.Empty;

                var tail_split = tail.Split(" // ");

                return (head, tail_split);
            })
            .Where(h => !string.IsNullOrWhiteSpace(h.head))
            .DistinctBy(h => h.head)
            .ToDictionary(h => h.head, h => h.tail_split);

        return classes;
    }

    private static async Task<Dictionary<string, string[]>> CreateGlyphsAsync(IEnumerable<string> glyphs, string path, AppSettings settings, CancellationToken token = default)
    {
        //Break glyphs into classes based on which ones are most similar
        HashSet<string> visited = [];
        Dictionary<string, List<string>> classes = [];

        var glyph_arr = glyphs.ToArray();

        //Create images for glyphs
        var glyph_images = Utils.GetGlyphImages(glyph_arr, settings.TileSize, settings.FontFace, settings.InvertFont)
            .ToDictionary(pair => pair.Key, pair => new PixelImage(pair.Value));

        SSIMCalculator calculator = new(glyph_images, configure =>
        {
            configure.Subdivide = settings.SubDivide;
            configure.GaussianStdDev = 1.5d;
        });

        foreach(var glyph in glyphs)
        {
            //If glyph already has been visited, it
            //already belongs to a class
            if(visited.Contains(glyph))
            {
                continue;
            }

            _ = visited.Add(glyph);

            PixelImage glyph_image = glyph_images[glyph];
            classes[glyph] = [glyph];

            //Compare each other glyph to this
            foreach(var other in glyphs)
            {
                //If glyph already has been visited, it
                //already belongs to a class
                if(visited.Contains(other))
                {
                    continue;
                }

                PixelImage other_image = glyph_images[other];

                //Do comparison
                var ssim = calculator.Calculate(glyph_image, other_image);

                //If SSIM is above a certain threshold, record as being in the same class
                if(ssim > CREATE_GLYPH_CLASSES_CONFIDENCE)
                {
                    _ = visited.Add(other);
                    classes[glyph].Add(other);
                }
            }
        }

        //Write to file
        await using FileStream fs = new(path, FileMode.OpenOrCreate, FileAccess.Write);
        using StreamWriter sw = new(fs);

        foreach(KeyValuePair<string, List<string>> pair in classes)
        {
            await sw.WriteLineAsync($"{pair.Key} ;; {string.Join(" // ", pair.Value)}");
        }

        return classes.ToDictionary(c => c.Key, c => c.Value.ToArray());
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

            if(shuffle)
            {
                temp_path = Directory.CreateTempSubdirectory();
                var shuffled = Path.Combine(temp_path.FullName, "preprocessed.txt");

                //Write the lines of all provided files to one file, shuffled
                using FileStream fs = new(shuffled, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                using StreamWriter sw = new(fs);

                foreach(var line in preprocessed_files
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

            foreach(var file in preprocessed_files)
            {
                using FileStream fs = new(file, FileMode.Open, FileAccess.Read);
                using StreamReader fsr = new(fs);

                string? line;
                while((line = fsr.ReadLine()) != null)
                {
                    var split = line.Split(" ; ");

                    if(split.Length > 0)
                    {
                        var intensities = split[0].Split(',')
                            .Select(s => double.TryParse(s, out var sf) ? sf : 0)
                            .ToArray();

                        var ssims = new double[glyphs.Length];

                        for(var i = 1; i < split.Length; i++)
                        {
                            var pair = split[i].Split(" , ", 2);

                            if(pair.Length == 2)
                            {
                                var glyph = pair[0];
                                var ssim = double.TryParse(pair[1], out var f) ? f : 0;

                                for(var g = 0; g < glyphs.Length; g++)
                                {
                                    if(glyphs[g].Equals(glyph))
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

    private static IEnumerable<(string glyph, uint? color)> ConvertImages(IImageToASCIIConverter converter, IEnumerable<string> paths)
    {
        //Process each file
        foreach(var file in paths.SelectMany(path => File.Exists(path)
                ? (new string[] { path })
                : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) : ([])).Distinct())
        {
            using FileStream fs = new(file, FileMode.Open, FileAccess.ReadWrite);

            foreach((string glyph, uint? color) pair in converter.ConvertImage(fs))
            {
                yield return pair;
            }
        }

        yield break;
    }

    private static IEnumerable<(double[] intensities, (string glyph, double ssim)[] scores)> ProcessImages(SSIMConverter converter, IEnumerable<string> paths,
        Action<string, uint?>? writer = null)
    {
        //Process each file
        foreach(var file in paths.SelectMany(path => File.Exists(path)
                ? (new string[] { path })
                : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) : ([])).Distinct())
        {
            using FileStream fs = new(file, FileMode.Open, FileAccess.Read);

            //Write each solution to preprocess file
            foreach((double[] intensities, (string glyph, double ssim)[] scores) solution in converter.ProcessImage(fs, writer))
            {
                yield return solution;
            }
        }

        yield break;
    }
}
