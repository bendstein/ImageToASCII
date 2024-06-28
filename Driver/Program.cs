using Driver;
using LibI2A;
using LibI2A.Common;
using LibI2A.Converter;
using LibI2A.SSIM;
using Microsoft.Data.Sqlite;
using Microsoft.ML;
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

    await using (var fs = new FileStream(config.Glyphs, FileMode.Open, FileAccess.Read))
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

    Dictionary<float[], List<(float ssim, string glyph)>> preprocessed = 
        new(StructuralEqualityComparer<float[]>.Default);

    //Read in solutions from file
    if (!string.IsNullOrWhiteSpace(config.Preprocessed) && File.Exists(config.Preprocessed))
    {
        await using var pps = new FileStream(config.Preprocessed, FileMode.Open, FileAccess.Read);
        using var ppsr = new StreamReader(pps);
        string? line;
        while ((line = await ppsr.ReadLineAsync()) != null)
        {
            var split = line.Split(';', 3);
            if (split.Length == 3)
            {
                var intensities = split[0].Split(',')
                    .Select(s => float.TryParse(s, out var sf) ? sf : 0)
                    .ToArray();
                var ssim = float.TryParse(split[1], out var sm) ? sm : 0;
                var glyph = split[2];

                if(!preprocessed.TryGetValue(intensities, out var values))
                {
                    values = [];
                    preprocessed[intensities] = values;
                }

                values.Add((ssim, glyph));
            }
        }
    }

    var store = new DictionarySSIMStore() { MaxKeys = (int)long.Clamp((long)preprocessed.Count + short.MaxValue, int.MinValue, int.MaxValue) };

    foreach ((var intensities, var value) in preprocessed)
        _ = store.GetOrCalculateAndStoreSoln(intensities.Select(i => (double)i).ToArray(), () => value.MaxBy(v => v.ssim).glyph);

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

        await using var pps = new FileStream(config.Preprocess, FileMode.OpenOrCreate, FileAccess.ReadWrite);
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

        foreach(var file in process_files)
        {
            await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);

            List<Thread> write_threads = [];

            foreach(var solution in converter.PreprocessImage(fs))
            {
                //Write should be independent of processing
                Thread write_thread = new(() =>
                {
                    lock(ppsw)
                        ppsw.WriteLine($"{string.Join(',', solution.intensities.Select(i => InternalUtils.Round(i, DictionarySSIMStore.PRECISION)))};{solution.ssim};{solution.glyph}");
                });
                write_thread.Start();
                write_threads.Add(write_thread);

                //Every once and awhile, cull complete threads
                if (write_threads.Count > 1000)
                    lock (write_threads)
                        write_threads.RemoveAll(t => t.ThreadState == ThreadState.Stopped);
            }

            //Wait for all write threads to complete
            foreach (var thread in write_threads)
                thread.Join();
        }
    }
    else if (config.Training)
    {
        List<string> training_dirs = [config.Path, ..config.TrainingDirs];

        //Chunk into groups of 2 images, sorted by name
        const int CHUNK_SIZE = 2;
        var files_chunks = training_dirs.Distinct()
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Distinct()
            .OrderBy(dir => dir)
            .Chunk(preprocessed.Count == 0? CHUNK_SIZE : int.MaxValue);

        ITransformer? existing_model = null;

        if (File.Exists(config.Model))
            existing_model = new MLContext().Model.Load(config.Model, out var schema);

        foreach (var files in files_chunks)
        {
            Console.WriteLine($"Training on chunk {string.Join(", ", files)}");
            using var ts = new PredictionModelSSIMConverter.TrainingSet()
            {
                FontFace = config.FontFace,
                FontSize = config.TileSize,
                Inputs = files.Select(file =>
                {
                    Console.WriteLine($"File: {file}");
                    return new FileStream(file, FileMode.Open, FileAccess.Read);
                }),
                Glyphs = glyphs,
                DisplayDir = config.TrainingDisplayDir,
                ParallelCalculate = config.ParallelCalculate,
                Preprocessed = preprocessed.Count == 0? [] : preprocessed.SelectMany(pair => pair.Value.Select(v => new PredictionModelSSIMConverter.ModelIn()
                {
                    Value = pair.Key,
                    Glyph = v.glyph,
                    SSIM = v.ssim
                }))
            };

            var result = PredictionModelSSIMConverter.Train(
                new SSIMCalculator(store, opts =>
                {
                    opts.Subdivide = config.SubDivide;
                }), store, ts, existing_model);

            //Save model and use as existing model for next chunk
            if (!string.IsNullOrWhiteSpace(config.Model))
            {
                Console.WriteLine($"Saving chunk to {config.Model}.");
                var dir_name = Path.GetDirectoryName(config.Model);

                if (!string.IsNullOrWhiteSpace(dir_name) && !Directory.Exists(dir_name))
                    Directory.CreateDirectory(dir_name);

                result.context.Model.Save(result.model, result.schema, config.Model);
            }

            Console.WriteLine($"Setting resulting model for chunk as previous model and moving to next chunk.");
            existing_model = result.model;
        }

        Console.WriteLine("Done training.");
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
            case "ML":
            default:
                var context = new MLContext();
                var model = context.Model.Load(config.Model, out var schema);
                var engine = context.Model.CreatePredictionEngine<PredictionModelSSIMConverter.ModelIn,
                    PredictionModelSSIMConverter.ModelOut>(model, schema);

                converter = new PredictionModelSSIMConverter(engine, new()
                {
                    FontFace = config.FontFace,
                    FontSize = config.TileSize,
                    InvertFont = config.InvertFont
                });
                break;
        }

#pragma warning disable SYSLIB1045 // Convert to 'GeneratedRegexAttribute'.
        var nl = new Regex(@"^[\n\r]+$");
#pragma warning restore SYSLIB1045 // Convert to 'GeneratedRegexAttribute'.

        using var image_stream = new FileStream(config.Path, FileMode.Open, FileAccess.Read);
        using var stream_writer = new StreamWriter(stream);

        foreach((var glyph, var color) in converter.ConvertImage(image_stream))
        {
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