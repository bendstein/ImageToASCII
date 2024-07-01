using Driver;
using LibI2A;
using LibI2A.Common;
using LibI2A.Converter;
using LibI2A.SSIM;
using Spectre.Console;
using Spectre.Console.Cli;
using System;
using System.Runtime;
using System.Text;

namespace ASCIIArt;
public class App : Command<AppSettings>
{
    private const string
        LAYOUT_ROOT = "Root",
        LAYOUT_LOG = "Log",
        LAYOUT_CANVAS = "Canvas",
        LAYOUT_TRAINING = "Training";

    private const int
        LIVE_UPDATE = 250;

    public override int Execute(CommandContext context, AppSettings settings)
    {
        Layout? layout = null;
        StringBuilder ascii = new();
        Table? log_table = null;
        int stop = 0;

        void Log(string message, bool is_error = false)
        {
            if (layout != null && log_table != null && layout.TryGetLayout(LAYOUT_LOG, out var log))
            {
                log_table.AddRow(DateTime.Now.ToString("HH:mm:ss.ffff"), is_error ? "[bold red]ERROR[/]" : "INFO", StringExtensions.EscapeMarkup(message));

                while (log_table.Rows.Count + 5 > AnsiConsole.Profile.Height)
                {
                    log_table.RemoveRow(0);
                }

                log.Update(log_table);
            }
        }

        void WriteGlyph(string glyph, uint? color = null)
        {
            if (layout != null && layout.TryGetLayout(LAYOUT_CANVAS, out var canvas))
            {
                string s = StringExtensions.EscapeMarkup(glyph);

                if (color != null && !settings.NoColor)
                {
                    int r = (int)(color >> 16) & 0xFF;
                    int g = (int)(color >> 8) & 0xFF;
                    int b = (int)color & 0xFF;
                    s = $"[rgb({r},{g},{b})]{s}[/]";
                }

                ascii.Append(s);

                canvas.Update(new Markup(ascii.ToString()));
            }
        }

        void LogNeuralNetData(NeuralNetConverter.DebugData data)
        {
            if(layout.TryGetLayout(LAYOUT_TRAINING, out var training_layout))
            {

            }
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
            layout = settings.Mode switch
            {
                Mode.preprocess => new Layout(LAYOUT_ROOT)
                    .SplitColumns(
                        new Layout(LAYOUT_CANVAS),
                        new Layout(LAYOUT_LOG)),
                Mode.train => new Layout(LAYOUT_ROOT)
                    .SplitColumns(
                        new Layout(LAYOUT_TRAINING),
                        new Layout(LAYOUT_LOG)),
                Mode.render or _ => new Layout(LAYOUT_CANVAS)
            };

            log_table = new();

            log_table.AddColumns("Timestamp", "Type", "Message");

            Log("Starting...");
            WriteGlyph(string.Empty);

            Console.CancelKeyPress += OnClose;

            SemaphoreSlim done = new(0, 1);

            Thread live_thread = new(() =>
            {
                AnsiConsole.Live(layout)
                    .AutoClear(false)
                    .Start(ctx =>
                    {
                        while (!done.Wait(LIVE_UPDATE))
                            ctx.Refresh();
                    });
            });
            live_thread.Start();

            try
            {
                switch (settings.Mode)
                {
                    //Preprocess images
                    case Mode.preprocess:
                    {
                        //Read glyphs file
                        var glyphs = LoadGlyphs(settings.Glyphs);

                        if(glyphs.Length == 0)
                            throw new Exception("No glyphs were present.");

                        var converter = new SSIMConverter(new NoSSIMStore(), opts =>
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
                            ascii.Clear();

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

                                preprocess_file_writer.WriteLine($"{string.Join(',', solution.intensities.Select(i => InternalUtils.Round(i, DictionarySSIMStore.PRECISION)))} ; {string.Join(" ; ", solution.scores.Select(s => $"{s.glyph} , {s.ssim}"))}");
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
                        //Read glyphs file
                        var glyphs = LoadGlyphs(settings.Glyphs);

                        if (glyphs.Length == 0)
                            throw new Exception("No glyphs were present.");

                        var converter = new SSIMConverter(new NoSSIMStore(), opts =>
                        {
                            opts.FontSize = settings.TileSize;
                            opts.FontFace = settings.FontFace;
                            opts.Subdivide = settings.SubDivide;
                            opts.ParallelCalculate = settings.Threads;
                            opts.NoColor = settings.NoColor;
                            opts.Glyphs = glyphs;
                            opts.InvertFont = settings.InvertFont;
                        });

                        //Load model
                        NeuralNetConverter.Model? model = null;

                        if (!string.IsNullOrWhiteSpace(settings.Model) && File.Exists(settings.Model))
                        {
                            Log($"Reading model from {settings.Model}.");

                            using var fs = new FileStream(settings.Model, FileMode.Open, FileAccess.Read);
                            using var ms = new MemoryStream();
                            fs.CopyTo(ms);
                            var bytes = ms.GetBuffer();
                            model = NeuralNetConverter.Model.FromBytes(bytes);
                        }

                        Log("Training...");

                        //Train
                        var preprocessed = LoadPreprocessed(settings.PreprocessedPath, glyphs);

                        foreach (var training_chunk in preprocessed.Chunk(50))
                        {
                            if (stop > 0)
                                break;

                            if (NeuralNetConverter.Train(new()
                            {
                                FeatureSize = settings.TileSize * settings.TileSize,
                                Glyphs = glyphs,
                                HiddenLayers = 2,
                                InitialModel = model,
                                TrainingRate = 1,
                                LearningRate = 1,
                                LearningRateDecay = 0.05,
                                TrainingData = training_chunk
                            }, Log, LogNeuralNetData, out var trained, out var error_message))
                            {
                                model = trained;
                            }
                            else
                            {
                                model = null;
                                throw new Exception($"Failed to train model. {error_message}");
                            }

                            //Save current model state
                            if(model != null && !string.IsNullOrWhiteSpace(settings.Model))
                            {
                                Log($"Writing model to {settings.Model}.");
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
                                NeuralNetConverter.Model? model = null;

                                if (!string.IsNullOrWhiteSpace(settings.Model) && File.Exists(settings.Model))
                                {
                                    Log($"Reading model from {settings.Model}.");

                                    using var fs = new FileStream(settings.Model, FileMode.Open, FileAccess.Read);
                                    using var ms = new MemoryStream();
                                    fs.CopyTo(ms);
                                    var bytes = ms.GetBuffer();
                                    model = NeuralNetConverter.Model.FromBytes(bytes);
                                }

                                if (model == null)
                                    throw new Exception($"Failed to find model at {settings.Model}");

                                converter = new NeuralNetConverter(model);
                            }
                            break;
                            case Method.ssim:
                            default:
                            {
                                converter = new SSIMConverter(new NoSSIMStore(), opts =>
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
            finally
            {
                done.Release();
            }

            live_thread.Join();
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
    private static IEnumerable<NeuralNetConverter.Input> LoadPreprocessed(string path, string[] glyphs)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            List<string> preprocessed_files = [];

            if (File.Exists(path))
            {
                preprocessed_files.Add(path);
            }
            else if (Directory.Exists(path))
            {
                preprocessed_files.AddRange(Directory.EnumerateFiles(path, "*.txt", SearchOption.AllDirectories));
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

        yield break;
    }
}
