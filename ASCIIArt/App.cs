using LibI2A;
using LibI2A.Common;
using LibI2A.Converter;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ASCIIArt;
public class App : Command<AppSettings>
{
    private const string
        LAYOUT_ROOT = "Root",
        LAYOUT_LOG = "Log",
        LAYOUT_CANVAS = "Canvas",
        LAYOUT_TRAINING = "Training";

    private const int
        LIVE_UPDATE = 250,
        PREPROCESS_PRECISION = 8;

    public override int Execute(CommandContext context, AppSettings settings)
    {
        int stop = 0;

        if (!Directory.Exists("log"))
            Directory.CreateDirectory("log");

        bool log_console = true;

        using var log_stream = new FileStream($"log/{DateTime.Now:MM-dd-yyyy-HH-mm-ss-ffff}.log", FileMode.OpenOrCreate, FileAccess.Write);
        using var log_writer = new StreamWriter(log_stream);

        void Log(string message, bool is_error = false)
        {
            var full_message = $"{DateTime.Now:HH:mm:ss.ffff} {(is_error ? "ERROR | " : "INFO  | ")} {message}";

            if(log_console)
                Console.WriteLine(full_message);

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
            if(stop > 0)
            {
                e.Cancel = false;
                return;
            }

            stop++;
            e.Cancel = true;
            Log("Stopping...");
        }

        try
        {
            Console.CancelKeyPress += OnClose;

            try
            {
                switch (settings.Mode)
                {
                    //Preprocess images
                    case Mode.preprocess:
                    {
                        log_console = false;

                        Log("Starting...");
                        WriteGlyph(string.Empty);

                        //Read glyphs file
                        var glyphs = LoadGlyphs(settings.Glyphs);

                        if(glyphs.Length == 0)
                            throw new Exception("No glyphs were present.");

                        var converter = new SSIMConverter(opts =>
                        {
                            opts.FontSize = settings.TileSize;
                            opts.FontFace = settings.FontFace;
                            opts.Subdivide = settings.SubDivide;
                            opts.ParallelCalculate = settings.Threads;
                            opts.NoColor = settings.NoColor;
                            opts.Glyphs = glyphs;
                            opts.InvertFont = settings.InvertFont;
                        });

                        var preprocess_dir = Path.GetDirectoryName(settings.PreprocessPath);

                        //Create directory if not exists
                        if (!string.IsNullOrWhiteSpace(preprocess_dir) && !Directory.Exists(preprocess_dir))
                            Directory.CreateDirectory(preprocess_dir);

                        //Open file writer
                        using var preprocess_file_stream = new FileStream(settings.PreprocessPath, FileMode.OpenOrCreate, FileAccess.Write);
                        using var preprocess_file_writer = new StreamWriter(preprocess_file_stream);

                        IEnumerable<string> paths = [];

                        //Process each file
                        foreach(var file in settings.Path.SelectMany(path =>
                        {
                            if (File.Exists(path))
                                return new string[] { path };
                            if (Directory.Exists(path))
                                return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
                            return [];
                        }).Distinct())
                        {
                            if (stop > 0)
                                break;

                            Log($"Preprocessing image {file}...");
                            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);

                            int s = 0;

                            //Write each solution to preprocess file
                            foreach(var solution in converter.ProcessImage(fs, WriteGlyph))
                            {
                                if (stop > 0)
                                    break;

                                if (s > 0 && s % 100 == 0)
                                    Log($"Preprocessing image {file}...");

                                preprocess_file_writer.WriteLine($"{string.Join(',', solution.intensities.Select(i => InternalUtils.Round(i, PREPROCESS_PRECISION)))} ; {string.Join(" ; ", solution.scores.Select(s => $"{s.glyph} , {s.ssim}"))}");
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
                        Log("Starting...");
                        WriteGlyph(string.Empty);

                        //Load model
                        NNConverter.Model? model = null;

                        if (!string.IsNullOrWhiteSpace(settings.Model) && File.Exists(settings.Model))
                        {
                            Log($"Reading model from {settings.Model}.");

                            using var fs = new FileStream(settings.Model, FileMode.Open, FileAccess.Read);
                            using var ms = new MemoryStream();
                            fs.CopyTo(ms);
                            var bytes = ms.GetBuffer();
                            model = NNConverter.Model.FromBytes(bytes);
                        }

                        //Read glyphs file
                        var glyphs = model == null? LoadGlyphs(settings.Glyphs) : model.Glyphs;

                        if (glyphs.Length == 0)
                            throw new Exception("No glyphs were present.");

                        var converter = new SSIMConverter(opts =>
                        {
                            opts.FontSize = settings.TileSize;
                            opts.FontFace = settings.FontFace;
                            opts.Subdivide = settings.SubDivide;
                            opts.ParallelCalculate = settings.Threads;
                            opts.NoColor = settings.NoColor;
                            opts.Glyphs = glyphs;
                            opts.InvertFont = settings.InvertFont;
                        });

                        Log("Training...");

                        //Train
                        var preprocessed = LoadPreprocessed(settings.PreprocessedPath, glyphs, settings.Shuffle);

                        var batches = preprocessed
                            .Where(item => item.Intensities.Length == settings.TileSize * settings.TileSize)
                            .Chunk(128)
                            .GetEnumerator();

                        for(int epoch = 0; batches.MoveNext(); epoch++)
                        {
                            if (stop > 0)
                                break;

                            var training_set = new NNConverter.TrainingSet()
                            {
                                Epoch = epoch,
                                Input = batches.Current,
                                LearningRate = settings.LearningRate,
                                LearningDecay = settings.LearningDecay,
                                Threads = settings.Threads
                            };

                            if(model == null)
                            {
                                if (NNConverter.Train(new NNConverter.ModelInitParams()
                                {
                                    FeatureCount = (uint)(settings.TileSize * settings.TileSize),
                                    Glyphs = glyphs,
                                    HiddenLayerCount = (uint)settings.HiddenLayers,
                                    HiddenLayerNeuronCount = (uint)Math.Max(1, settings.HiddenNeurons),
                                    Alpha = settings.ReLUAlpha
                                }, training_set, Log, out var trained, out var error_message))
                                {
                                    model = trained;
                                }
                                else
                                {
                                    model = null;
                                    throw new Exception($"Failed to train model. {error_message}");
                                }
                            } 
                            else
                            {
                                if (NNConverter.Train(model, training_set, Log, out var trained, out var error_message))
                                {
                                    model = trained;
                                }
                                else
                                {
                                    model = null;
                                    throw new Exception($"Failed to train model. {error_message}");
                                }
                            }

                            //Save current model state
                            if (model != null && !string.IsNullOrWhiteSpace(settings.Model))
                            {
                                var model_dir = Path.GetDirectoryName(settings.Model);

                                if (!string.IsNullOrWhiteSpace(model_dir) && !Directory.Exists(model_dir))
                                    Directory.CreateDirectory(model_dir);

                                //Log($"Writing model to {settings.Model}.");
                                using var fs = new FileStream(settings.Model, FileMode.Create, FileAccess.ReadWrite);
                                var model_bytes = model.ToBytes();
                                fs.Write(model_bytes);
                            }
                        }
                    }
                    break;
                    //Render the image as ASCII
                    case Mode.render:
                    default:
                    {
                        log_console = false;

                        Log("Starting...");
                        WriteGlyph(string.Empty);

                        //Read glyphs file
                        var glyphs = LoadGlyphs(settings.Glyphs);

                        if (glyphs.Length == 0)
                            throw new Exception("No glyphs were present.");

                        IImageToASCIIConverter converter;

                        switch(settings.Method)
                        {
                            case Method.model:
                            {
                                //Load model
                                NNConverter.Model? model = null;

                                if (!string.IsNullOrWhiteSpace(settings.Model) && File.Exists(settings.Model))
                                {
                                    Log($"Reading model from {settings.Model}.");

                                    using var fs = new FileStream(settings.Model, FileMode.Open, FileAccess.Read);
                                    using var ms = new MemoryStream();
                                    fs.CopyTo(ms);
                                    var bytes = ms.GetBuffer();
                                    model = NNConverter.Model.FromBytes(bytes);
                                }

                                if (model == null)
                                    throw new Exception($"Failed to find model at {settings.Model}");

                                converter = new NNConverter(model, opts =>
                                {
                                    opts.FontSize = settings.TileSize;
                                    opts.NoColor = settings.NoColor;
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
                                    opts.NoColor = settings.NoColor;
                                    opts.Glyphs = glyphs;
                                    opts.InvertFont = settings.InvertFont;
                                });
                            }
                            break;
                        }

                        //Process each file
                        foreach (var file in settings.Path.SelectMany(path =>
                        {
                            if (File.Exists(path))
                                return new string[] { path };
                            if (Directory.Exists(path))
                                return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
                            return [];
                        }).Distinct())
                        {
                            if (stop > 0)
                                break;

                            using var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite);

                            foreach((var glyph, var color) in converter.ConvertImage(fs))
                            {
                                WriteGlyph(glyph, color);
                            }
                        }
                    }
                    break;
                }
            }
            catch (Exception e)
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

    private static string[] LoadGlyphs(string path)
    {
        //Read glyphs file
        string[] glyphs;

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            glyphs = Utils.ReadGlyphsFile(fs);

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

            if(shuffle)
            {
                temp_path = Directory.CreateTempSubdirectory();
                string shuffled = Path.Combine(temp_path.FullName, "preprocessed.txt");

                //Write the lines of all provided files to one file, shuffled
                using var fs = new FileStream(shuffled, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                using var sw = new StreamWriter(fs);

                foreach (var line in preprocessed_files
                    .SelectMany(File.ReadAllLines)
                    .OrderBy(_ => Random.Shared.Next())
                    .ToArray())
                    sw.WriteLine(line);

                sw.Flush();
                fs.Flush();

                preprocessed_files = [shuffled];
            }

            foreach (var file in preprocessed_files)
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
                using var fsr = new StreamReader(fs);

                string? line;
                while ((line = fsr.ReadLine()) != null)
                {
                    var split = line.Split(" ; ");

                    if (split.Length > 0)
                    {
                        var intensities = split[0].Split(',')
                            .Select(s => double.TryParse(s, out var sf) ? sf : 0)
                            .ToArray();

                        double[] ssims = new double[glyphs.Length];

                        for (int i = 1; i < split.Length; i++)
                        {
                            var pair = split[i].Split(" , ", 2);

                            if (pair.Length == 2)
                            {
                                var glyph = pair[0];
                                var ssim = double.TryParse(pair[1], out var f) ? f : 0;

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
