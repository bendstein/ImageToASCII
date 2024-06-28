namespace Driver;

public class Config
{
    private const string ARG_PREFIX = "--";

    private const string GLYPHS_DEFAULT = "glyphs.txt";

    private const int DEFAULT_TILE_SIZE = 12;

    private const int DEFAULT_CHAR_WIDTH = 1;

    private const string DEFAULT_FONT_FACE = "Consolas";

    private const string DEFAULT_TRAINING_DISPLAY_DIR = "training_display";

    private const string DEFAULT_DB = "i2a.db";

    private static readonly string[] FLAG_ONLY = [
        nameof(Training).ToLowerInvariant(),
        nameof(NoColor).ToLowerInvariant(),
        nameof(SetVT).ToLowerInvariant(),
        nameof(FlushCache).ToLowerInvariant(),
        nameof(WriteAll).ToLowerInvariant(),
        nameof(InvertFont).ToLowerInvariant(),
    ];

    public string Path { get; set; } = string.Empty;

    public string Glyphs { get; set; } = GLYPHS_DEFAULT;

    public int TileSize { get; set; } = DEFAULT_TILE_SIZE;

    public string FontFace { get; set; } = DEFAULT_FONT_FACE;

    public int SubDivide { get; set; } = 0;

    public bool NoColor { get; set; } = false;

    public bool InvertFont { get; set; } = false;

    public int CharWidth { get; set; } = 1;

    public bool SetVT { get; set; } = false;

    public bool FlushCache { get; set; } = false;

    public (int? w, int? h) Clamp { get; set; } = (null, null);

    public int Repeat { get; set; } = 0;

    public int ParallelCalculate { get; set; } = 0;

    public bool WriteAll { get; set; } = false;

    public string Output { get; set; } = string.Empty;

    public bool Training { get; set; } = false;

    public int TrainingParallel { get; set; } = 1;

    public List<string> TrainingDirs { get; set; } = [];

    public string Model { get; set; } = string.Empty;

    public string TrainingDisplayDir { get; set; } = DEFAULT_TRAINING_DISPLAY_DIR;

    public string DB { get; set; } = DEFAULT_DB;

    public string Method { get; set; } = string.Empty;

    public string Preprocess { get; set; } = string.Empty;

    public string Preprocessed { get; set; } = string.Empty;

    public Dictionary<string, string?> Arguments = [];

    public static Config ParseArgs(params string[] args)
    {
        Config config = new();

        string? prev_arg = null;
        bool seen_arg = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].ToLowerInvariant();

            if (arg.StartsWith(ARG_PREFIX) && !arg.Equals(ARG_PREFIX))
            {
                seen_arg = true;

                arg = arg[ARG_PREFIX.Length..];

                if (config.Arguments.ContainsKey(arg))
                    throw new Exception($"Argument {ARG_PREFIX}{arg} has already been specified.");

                //Unless argument is explicitly only allowed to be a flag, at next arg check for value
                if (!FLAG_ONLY.Contains(arg))
                    prev_arg = arg;
                else
                    prev_arg = null;

                config.Arguments[arg] = null;
            }
            else
            {
                //Handle positional args
                if (!seen_arg)
                {
                    switch (i)
                    {
                        case 0:
                            config.Arguments[nameof(Path).ToLowerInvariant()] = arg;
                            break;
                        case 1:
                            config.Arguments[nameof(Glyphs).ToLowerInvariant()] = arg;
                            break;
                        default:
                            throw new Exception($"Unexpected argument '{arg}'");
                    }
                }
                //Value for previous arg
                else if (prev_arg != null)
                {
                    config.Arguments[prev_arg] = arg;
                }
                //Invalid argument
                else
                {
                    throw new Exception($"Unexpected argument '{arg}'");
                }

                prev_arg = null;
            }
        }

        config.Path = (config.Arguments.TryGetValue(nameof(Path).ToLowerInvariant(), out var path) ? path
            : null) ?? throw new Exception($"Path is required.");

        config.Output = (config.Arguments.TryGetValue(nameof(Output).ToLowerInvariant(), out var output) ? output
            : null) ?? string.Empty;

        config.TrainingDisplayDir = (config.Arguments.TryGetValue(nameof(TrainingDisplayDir).ToLowerInvariant(), out var tdd) ? tdd
            : null) ?? DEFAULT_TRAINING_DISPLAY_DIR;

        config.Glyphs = (config.Arguments.TryGetValue(nameof(Glyphs).ToLowerInvariant(), out var glyphs) ? glyphs
            : null) ?? GLYPHS_DEFAULT;

        config.Model = (config.Arguments.TryGetValue(nameof(Model).ToLowerInvariant(), out var model) ? model
            : null) ?? string.Empty;

        config.Method = (config.Arguments.TryGetValue(nameof(Method).ToLowerInvariant(), out var method) ? method
            : null) ?? string.Empty;

        config.DB = (config.Arguments.TryGetValue(nameof(DB).ToLowerInvariant(), out var db) ? db
            : null) ?? DEFAULT_DB;

        config.Preprocessed = (config.Arguments.TryGetValue(nameof(Preprocessed).ToLowerInvariant(), out var ppcd) ? ppcd
            : null) ?? string.Empty;

        config.Preprocess = (config.Arguments.TryGetValue(nameof(Preprocess).ToLowerInvariant(), out var ppc) ? ppc
               : null) ?? string.Empty;

        config.Training = config.Arguments.ContainsKey(nameof(Training).ToLowerInvariant());

        config.InvertFont = config.Arguments.ContainsKey(nameof(InvertFont).ToLowerInvariant());

        config.TileSize = int.TryParse((config.Arguments.TryGetValue(nameof(TileSize).ToLowerInvariant(), out var ts) ? ts : DEFAULT_TILE_SIZE.ToString()) ?? DEFAULT_TILE_SIZE.ToString(), out var tsi) ? tsi : 0;

        if (config.TileSize <= 0)
            throw new Exception($"Invalid tile size {config.TileSize}");

        config.SubDivide = int.TryParse((config.Arguments.TryGetValue(nameof(SubDivide).ToLowerInvariant(), out var sd) ? sd : 0.ToString()) ?? 0.ToString(), out var sdi) ? sdi : 0;

        if (config.SubDivide < 0)
            throw new Exception($"Invalid subdivision {config.SubDivide}");

        config.TrainingParallel = int.TryParse((config.Arguments.TryGetValue(nameof(TrainingParallel).ToLowerInvariant(), out var tp) ? tp : 1.ToString()) ?? 1.ToString(), out var tpi) ? tpi : 1;

        config.FontFace = (config.Arguments.TryGetValue(nameof(FontFace).ToLowerInvariant(), out var ff) ? ff
        : null) ?? DEFAULT_FONT_FACE;

        config.NoColor = config.Arguments.ContainsKey(nameof(NoColor).ToLowerInvariant());

        config.SetVT = config.Arguments.ContainsKey(nameof(SetVT).ToLowerInvariant());

        config.FlushCache = config.Arguments.ContainsKey(nameof(FlushCache).ToLowerInvariant());

        config.WriteAll = config.Arguments.ContainsKey(nameof(WriteAll).ToLowerInvariant());

        config.CharWidth = int.TryParse((config.Arguments.TryGetValue(nameof(CharWidth).ToLowerInvariant(), out var cw) ? cw : DEFAULT_CHAR_WIDTH.ToString()) ?? DEFAULT_CHAR_WIDTH.ToString(), out var cwi) ? cwi : 0;

        if (config.CharWidth <= 0)
            throw new Exception($"Invalid character width {config.CharWidth}");

        config.Repeat = int.TryParse((config.Arguments.TryGetValue(nameof(Repeat).ToLowerInvariant(), out var rp) ? rp : 0.ToString()) ?? 0.ToString(), out var rpi) ? rpi : 0;

        config.ParallelCalculate = int.TryParse((config.Arguments.TryGetValue(nameof(ParallelCalculate).ToLowerInvariant(), out var pc) ? pc : 1.ToString()) ?? 1.ToString(), out var pci) ? pci : 1;

        if (config.Arguments.TryGetValue(nameof(Clamp).ToLowerInvariant(), out var _clamp) && _clamp != null)
        {
            string[] split = _clamp.Split(',', StringSplitOptions.TrimEntries);

            config.Clamp = (
                (split.Length > 0 && int.TryParse(split[0], out var w) && w > 0) ? w : null,
                (split.Length > 1 && int.TryParse(split[1], out var h) && h > 0) ? h : null);
        }

        if(config.Arguments.TryGetValue(nameof(TrainingDirs), out var tds))
        {
            config.TrainingDirs = tds!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(Directory.Exists)
                .ToList();
        }

        return config;
    }
}