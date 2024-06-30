using Driver;
using LibI2A;
using LibI2A.Common;
using LibI2A.Converter;
using LibI2A.SSIM;
using Pastel;
using System.Text;
using System.Text.RegularExpressions;

try
{
    var config = Config.ParseArgs(args);

    ////Initialize database
    //bool init = File.Exists(config.DB);

    //using var connection = new SqliteConnection($"Data Source={config.DB}");

    //connection.Open();

    //Make sure the image exists
    if(!config.Training)
    {
        if (!File.Exists(config.Path))
            throw new Exception($"Image not found at '{config.Path}'.");
    }

    //Make sure the glyphs file exists
    if (!File.Exists(config.Glyphs))
        throw new Exception($"Glyphs file not found at '{config.Glyphs}'.");

    //Set VT if specified
    if (config.SetVT)
    {
        try
        {
            ConsoleUtils.SetVTMode(out _);
        }
        catch { }
    }

    //Read glyphs file
    string[] glyphs;

    using (var fs = new FileStream(config.Glyphs, FileMode.Open, FileAccess.Read))
        glyphs = Utils.ReadGlyphsFile(fs);

    if (glyphs.Length == 0)
        throw new Exception($"No glyphs were present.");

    string output_file = config.Output;

    if(!config.Training && !string.IsNullOrWhiteSpace(output_file))
    {
        string dir = Path.GetDirectoryName(output_file)?? string.Empty;

        if(!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        string filename = Path.GetFileNameWithoutExtension(output_file)?? string.Empty;
        string ext = Path.GetExtension(output_file)?? string.Empty;

        for(int i = 1; File.Exists(output_file); i++)
            output_file = Path.Combine(dir, $"{filename} ({i}){ext}".Trim());
    }

    Console.Clear();

    void Log(string message, bool is_error = false)
    {
        var now = DateTime.Now;

        if (is_error)
            Console.Error.WriteLine($"{now:HH:mm:ss.ffff} [ERROR] | {message}");
        else
            Console.WriteLine($"{now:HH:mm:ss.ffff} [INFO]  | {message}");
    }

    int stop = 0;

    Console.CancelKeyPress += (sender, e) =>
    {
        e.Cancel = true;
        Log("Stopping...");
        stop++;
    };

    //Read in solutions from file
    IEnumerable<NeuralNetConverter.Input> LoadPreprocessed()
    {
        if (!string.IsNullOrWhiteSpace(config.Preprocessed))
        {
            List<string> preprocessed_files = [];

            if (File.Exists(config.Preprocessed))
            {
                preprocessed_files.Add(config.Preprocessed);
            }
            else if (Directory.Exists(config.Preprocessed))
            {
                preprocessed_files.AddRange(Directory.EnumerateFiles(config.Preprocessed, "*.txt", SearchOption.AllDirectories));
            }

            foreach (var file in preprocessed_files)
            {
                if (stop > 0)
                    yield break;

                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
                using var fsr = new StreamReader(fs);

                string? line;
                while ((line = fsr.ReadLine()) != null)
                {
                    if (stop > 0)
                        yield break;

                    var split = line.Split(" ; ");

                    if (split.Length > 0)
                    {
                        var intensities = split[0].Split(" , ")
                            .Select(s => double.TryParse(s, out var sf) ? sf : 0)
                            .ToArray();

                        double[] ssims = new double[glyphs.Length];

                        for (int i = 1; i < split.Length; i++)
                        {
                            if (stop > 0)
                                yield break;

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

    if (stop > 0)
        return;

    var store = new NoSSIMStore();

    List<Stream> streams = [];

    if (!config.Training && !string.IsNullOrWhiteSpace(output_file))
        streams.Add(new FileStream(output_file, FileMode.CreateNew, FileAccess.Write));

    using var stream = new UnionStream(streams);

    //Preprocess images
    if (!string.IsNullOrWhiteSpace(config.Preprocess))
    {
        List<string> process_dirs = [config.Path, .. config.TrainingDirs];
        var process_files = process_dirs.Distinct()
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Distinct()
            .OrderBy(dir => dir);

        using var pps = new FileStream(config.Preprocess, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        using var ppsw = new StreamWriter(pps);

        var converter = new SSIMConverter(store, opts =>
        {
            opts.FontSize = config.TileSize;
            opts.FontFace = config.FontFace;
            opts.Subdivide = config.SubDivide;
            opts.ParallelCalculate = config.ParallelCalculate;
            opts.Glyphs = glyphs;
            opts.InvertFont = config.InvertFont;
        });

        foreach (var file in process_files)
        {
            if (stop > 0)
                break;

            Log($"Preprocessing image {file}.");

            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);

            foreach(var solution in converter.ProcessImage(fs))
            {
                if (stop > 0)
                    break;

                ppsw.WriteLine($"{string.Join(',', solution.intensities.Select(i => InternalUtils.Round(i, DictionarySSIMStore.PRECISION)))} ; {string.Join(" ; ", solution.scores.Select(s => $"{s.glyph} , {s.ssim}"))}");
            }
        }

        if (stop > 0)
            return;
    }
    else if (config.Training)
    {
        List<string> training_dirs = [config.Path, ..config.TrainingDirs];

        var preprocessed = LoadPreprocessed();

        //Chunk into groups of 2 images, sorted by name
        const int CHUNK_SIZE = 1;
        var files_chunks = training_dirs.Distinct()
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Distinct()
            .OrderBy(dir => dir)
            .Chunk(preprocessed.Any()? int.MaxValue : CHUNK_SIZE);

        NeuralNetConverter.Model? model = null;

        if(!string.IsNullOrWhiteSpace(config.Model) && File.Exists(config.Model))
        {
            Log($"Reading model from {config.Model}.");

            using var fs = new FileStream(config.Model, FileMode.Open, FileAccess.Read);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            var bytes = ms.GetBuffer();
            model = NeuralNetConverter.Model.FromBytes(bytes);
        }

        Log("Training...");

        foreach (var files in files_chunks)
        {
            if (stop > 0)
                break;

            var training_converter = new SSIMConverter(store, opts =>
            {
                opts.FontSize = config.TileSize;
                opts.FontFace = config.FontFace;
                opts.Subdivide = config.SubDivide;
                opts.ParallelCalculate = config.ParallelCalculate;
                opts.Glyphs = glyphs;
                opts.InvertFont = config.InvertFont;
            });

            IEnumerable<NeuralNetConverter.Input> enumerateInput()
            {
                if(preprocessed.Any())
                {
                    foreach (var datum in preprocessed)
                    {
                        if (stop > 0)
                            yield break;

                        yield return datum;
                    }
                    yield break;
                }

                foreach (var file in files)
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);

                    if (!Directory.Exists("log"))
                        Directory.CreateDirectory("log");

                    using var ls = new FileStream($"log/{Path.GetFileNameWithoutExtension(file)}.log", FileMode.OpenOrCreate, FileAccess.ReadWrite);
                    using var lsw = new StreamWriter(ls);

                    var data = training_converter.ProcessImage(fs, lsw);

                    foreach (var datum in data)
                    {
                        double[] ssims = new double[glyphs.Length];

                        for (int i = 0; i < datum.scores.Length; i++)
                        {
                            (var glyph, var ssim) = datum.scores[i];

                            for (int g = 0; g < glyphs.Length; g++)
                            {
                                if (glyphs[g].Equals(glyph))
                                {
                                    ssims[g] = ssim;
                                    break;
                                }
                            }
                        }

                        if (stop > 0)
                            yield break;

                        yield return new NeuralNetConverter.Input()
                        {
                            Intensities = datum.intensities,
                            SSIMs = ssims
                        };
                    }

                    if (stop > 0)
                        yield break;
                }

                yield break;
            }

            if (NeuralNetConverter.Train(new()
            {
                FeatureSize = config.TileSize * config.TileSize,
                Glyphs = glyphs,
                HiddenLayers = 2,
                LearningRate = 1,
                LearningRateDecay = 0.005,
                TrainingRate = 1,
                InitialModel = model,
                TrainingData = enumerateInput()
            }, Log, out var trained, out var error_message))
            {
                model = trained;
            }
            else
            {
                model = null;
                throw new Exception($"Failed to train model. {error_message}");
            }
        }

        if(model != null && !string.IsNullOrWhiteSpace(config.Model))
        {
            Log($"Writing model to {config.Model}.");
            using var fs = new FileStream(config.Model, FileMode.Create, FileAccess.ReadWrite);
            var model_bytes = model.ToBytes();
            fs.Write(model_bytes);
        }

        Log("Done training.");
    }
    else
    {
        IImageToASCIIConverter converter;
        switch(config.Method.ToUpper().Trim())
        {
            case "SSIM":
                converter = new SSIMConverter(store, opts =>
                {
                    opts.FontSize = config.TileSize;
                    opts.FontFace = config.FontFace;
                    opts.Subdivide = config.SubDivide;
                    opts.ParallelCalculate = config.ParallelCalculate;
                    opts.Glyphs = glyphs;
                    opts.InvertFont = config.InvertFont;
                });
                break;
            case "NN":
            default:
                NeuralNetConverter.Model? model = null;

                if (!string.IsNullOrWhiteSpace(config.Model) && File.Exists(config.Model))
                {
                    Log($"Reading model from {config.Model}.");

                    using var fs = new FileStream(config.Model, FileMode.Open, FileAccess.Read);
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    var bytes = ms.GetBuffer();
                    model = NeuralNetConverter.Model.FromBytes(bytes);
                }

                if (model == null)
                    throw new Exception($"No model present.");

                converter = new NeuralNetConverter(model);
                break;
        }

#pragma warning disable SYSLIB1045 // Convert to 'GeneratedRegexAttribute'.
        var nl = new Regex(@"^[\n\r]+$");
#pragma warning restore SYSLIB1045 // Convert to 'GeneratedRegexAttribute'.

        using var image_stream = new FileStream(config.Path, FileMode.Open, FileAccess.Read);
        using var stream_writer = new StreamWriter(stream);

        foreach((var glyph, var color) in converter.ConvertImage(image_stream))
        {
            if (stop > 0)
                return;

            var repeat = nl.IsMatch(glyph) ? 1 : Math.Max(1, config.Repeat);

            for(int i = 0; i < repeat; i++)
            {
                Console.Write(config.NoColor ? glyph : Color(glyph, color));
                stream_writer.Write(glyph);
            }
        }
    }   
}
catch (Exception e)
{
    Console.Error.WriteLine($"An error occurred: {e.Message}\r\n{e.StackTrace}");
}

static string Color(string s, uint? color = null)
{
    System.Drawing.Color? c = null;

    if (color != null)
    {
        c = System.Drawing.Color.FromArgb(
            (int)(color >> 24) & 0xFF,
            (int)(color >> 16) & 0xFF,
            (int)(color >> 8) & 0xFF,
            (int)color & 0xFF);
    }

    StringBuilder sb = new();

    string cs = s;

    if (c.HasValue)
        cs = cs.Pastel(c.Value);

    sb.Append(cs);

    return sb.ToString();
}