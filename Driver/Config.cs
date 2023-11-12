using LibI2A.Converters;

namespace Driver;

public class Config
{
    private const string ARG_PREFIX = "--";

    private const string GLYPHS_DEFAULT = "glyphs.txt";

    private const int DEFAULT_TILE_SIZE = 12;

    private const int DEFAULT_FONT_SIZE = 12;
    
    private const int DEFAULT_CHAR_WIDTH = 1;

    private const string DEFAULT_FONT_FACE = "Consolas";

    private static readonly string[] FLAG_ONLY = new string[] { nameof(RecalculateGlyphs).ToLowerInvariant() };

    public string Path { get; set; } = string.Empty;

    public string Glyphs { get; set; } = GLYPHS_DEFAULT;

    public bool RecalculateGlyphs { get; set; } = false;

    public int TileSize { get; set; } = DEFAULT_TILE_SIZE;

    public int FontSize { get; set; } = DEFAULT_FONT_SIZE;

    public string FontFace { get; set; } = DEFAULT_FONT_FACE;

    public bool NoColor { get; set; } = false;

    public int CharWidth { get; set; } = 1;

    public bool SetVT { get; set; } = false;

    public bool FlushCache { get; set; } = false;

    public (double luminance, double contrast, double structure) SSIMCoeffs { get; set; } = SSIMSettings.DEFAULT_COEFFS;

    public (double luminance, double contrast, double structure) SSIMWeights { get; set; } = SSIMSettings.DEFAULT_WEIGHTS;

    public (int? w, int? h) Clamp { get; set; } = (null, null);

    public int Repeat { get; set; } = 0;

    public bool WriteAll { get; set; } = false;

    public Dictionary<string, string?> Arguments = new();

    public static Config ParseArgs(params string[] args)
    {
        Config config = new();

        string? prev_arg = null;
        bool seen_arg = false;

        for(int i = 0; i < args.Length; i++)
        {
            string arg = args[i].ToLowerInvariant();

            if(arg.StartsWith(ARG_PREFIX) && !arg.Equals(ARG_PREFIX))
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
                if(!seen_arg)
                {
                    switch(i)
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
                else if(prev_arg != null)
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

        config.Path = (config.Arguments.TryGetValue(nameof(Path).ToLowerInvariant(), out var path)? path
            : null)?? throw new Exception($"Path is required.");

        config.Glyphs = (config.Arguments.TryGetValue(nameof(Glyphs).ToLowerInvariant(), out var glyphs) ? glyphs
            : null)?? GLYPHS_DEFAULT;

        config.RecalculateGlyphs = config.Arguments.ContainsKey(nameof(RecalculateGlyphs).ToLowerInvariant());

        config.TileSize = int.TryParse((config.Arguments.TryGetValue(nameof(TileSize).ToLowerInvariant(), out var ts) ? ts : DEFAULT_TILE_SIZE.ToString()) ?? DEFAULT_TILE_SIZE.ToString(), out var tsi) ? tsi : 0;

        if (config.TileSize <= 0)
            throw new Exception($"Invalid tile size {config.TileSize}");

        config.FontSize = int.TryParse((config.Arguments.TryGetValue(nameof(FontSize).ToLowerInvariant(), out var fs) ? fs : DEFAULT_FONT_SIZE.ToString()) ?? DEFAULT_FONT_SIZE.ToString(), out var fsi) ? fsi : 0;

        if (config.FontSize <= 0)
            throw new Exception($"Invalid font size {config.FontSize}");

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

        if (config.Arguments.TryGetValue(nameof(SSIMCoeffs).ToLowerInvariant(), out var _ssimcoeffs) && _ssimcoeffs != null)
        {
            string[] split = _ssimcoeffs.Split(',', StringSplitOptions.TrimEntries);

            if (split.Length != 3)
                throw new Exception($"Expected 3 values for SSIM Coefficients. Got: {split.Length}");

            config.SSIMCoeffs = (
                double.TryParse(split[0], out var l)? l : throw new Exception($"Invalid luminance coefficient {split[0]}"),
                double.TryParse(split[1], out var c)? c : throw new Exception($"Invalid contrast coefficient {split[1]}"),
                double.TryParse(split[2], out var s)? s : throw new Exception($"Invalid strucutre coefficient {split[2]}"));
        }

        if (config.Arguments.TryGetValue(nameof(SSIMWeights).ToLowerInvariant(), out var _ssimweights) && _ssimweights != null)
        {
            string[] split = _ssimweights.Split(',', StringSplitOptions.TrimEntries);

            if (split.Length != 3)
                throw new Exception($"Expected 3 values for SSIM Weights. Got: {split.Length}");

            config.SSIMWeights = (
                double.TryParse(split[0], out var l) ? l : throw new Exception($"Invalid luminance weight {split[0]}"),
                double.TryParse(split[1], out var c) ? c : throw new Exception($"Invalid contrast weight {split[1]}"),
                double.TryParse(split[2], out var s) ? s : throw new Exception($"Invalid strucutre weight {split[2]}"));
        }

        if (config.Arguments.TryGetValue(nameof(Clamp).ToLowerInvariant(), out var _clamp) && _clamp != null)
        {
            string[] split = _clamp.Split(',', StringSplitOptions.TrimEntries);

            config.Clamp = (
                (split.Length > 0 && int.TryParse(split[0], out var w) && w > 0) ? w : null,
                (split.Length > 1 && int.TryParse(split[1], out var h) && h > 0) ? h : null);
        }

        return config;
    }
}