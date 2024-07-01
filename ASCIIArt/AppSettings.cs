using System.ComponentModel;
using Spectre.Console.Cli;

namespace ASCIIArt;
public class AppSettings : CommandSettings
{
    public const string
        GLYPHS_DEFAULT = "glyphs.txt",
        FONT_FACE_DEFAULT = "Consolas",
        MODE_DEFAULT = "render",
        METHOD_DEFAULT = "ssim",
        PREPROCESS_PATH_DEFAULT = "preprocessed/preprocessed.txt",
        MODEL_PATH_DEFAULT = "model/model.nn";

    public const int
        TILE_SIZE_DEFAULT = 4,
        THREADS_DEFAULT = 8;

    [CommandArgument(0, "[path]")]
    [Description("The image file/directories to process.")]
    public string[] Path { get; set; } = [];

    [CommandOption("-g|--glyphs")]
    [DefaultValue(GLYPHS_DEFAULT)]
    [Description("The SSIM glyphs file to use.")]
    public string Glyphs { get; set; } = string.Empty;

    [CommandOption("-t|--tile-size")]
    [DefaultValue(TILE_SIZE_DEFAULT)]
    [Description("The length of the tile that will be replaced by a glyph.")]
    public int TileSize { get; set; } = TILE_SIZE_DEFAULT;

    [CommandOption("-f|--font-face")]
    [DefaultValue(FONT_FACE_DEFAULT)]
    [Description("The font face to use when calculating the similarity between a tile and glyph.")]
    public string FontFace { get; set; } = FONT_FACE_DEFAULT;

    [CommandOption("-s|--subdivide")]
    [DefaultValue(0)]
    [Description("The number of times to divide the image in half when calculating SSIM.")]
    public int SubDivide { get; set; } = 0;

    [CommandOption("-n|--no-color")]
    [DefaultValue(false)]
    [Description("Render the ASCII image without color.")]
    public bool NoColor { get; set; } = false;

    [CommandOption("-i|--invert")]
    [DefaultValue(false)]
    [Description("Calculate SSIM using white-on-black glyphs instead of black-on-white.")]
    public bool InvertFont { get; set; } = false;

    [CommandOption("-c|--clamp")]
    [Description("Clamp the resulting image to Width,Height characters.")]
    public string ClampString { get; set; } = string.Empty;

    public (int? w, int? h) Clamp
    {
        get
        {
            int?[] split = ClampString.Split(',', StringSplitOptions.TrimEntries)
                .Select(s =>
                {
                    if (int.TryParse(s, out var i) && i >= 0)
                        return (int?)i;
                    return null;
                }).ToArray();

            return (split.Length > 0? split[0] : null, split.Length > 1? split[1] : null);
        }
        set => ClampString = $"{(value.w.HasValue? value.w.ToString() : "")},{(value.h.HasValue ? value.h.ToString() : "")}";
    }

    [CommandOption("-m|--model")]
    [DefaultValue(MODEL_PATH_DEFAULT)]
    [Description("If using the trained model method, this is the path to the model.")]
    public string Model { get; set; } = string.Empty;

    [CommandOption("--mode")]
    [DefaultValue(MODE_DEFAULT)]
    [Description("render: Render the image as ASCII; " 
        + "train: Train a model to predict the appropriate glyph for a tile; " 
        + "preprocess: Collect SSIM data for later training.")]
    public string ModeString { get; set; } = string.Empty;

    public Mode Mode
    {
        get => Enum.TryParse(ModeString, out Mode m) ? m : Mode.render;
        set => ModeString = value.ToString();
    }

    [CommandOption("--threads")]
    [DefaultValue(THREADS_DEFAULT)]
    [Description("The maximum number of parallel threads to use while calculating SSIM between a tile and glyphs.")]
    public int Threads { get; set; } = THREADS_DEFAULT;

    public string MethodString { get; set; }= string.Empty;

    [CommandOption("--method")]
    [DefaultValue(METHOD_DEFAULT)]
    [Description("The way to determine similarity between a tile and a glyph in render mode. "
        + "ssim: Use the SSIM to determine similarity; model: Use a trained model to determine similarity.")]
    public Method Method
    {
        get => Enum.TryParse(MethodString, out Method m) ? m : Method.ssim;
        set => MethodString = value.ToString();
    }

    [CommandOption("--preprocess-path")]
    [DefaultValue(PREPROCESS_PATH_DEFAULT)]
    [Description("Output results to this file in preprocessing mode.")]
    public string PreprocessPath { get; set; } = PREPROCESS_PATH_DEFAULT;

    [CommandOption("--preprocessed-path")]
    [DefaultValue(PREPROCESS_PATH_DEFAULT)]
    [Description("Pull preprocessed data from this file/directory when in training mode.")]
    public string PreprocessedPath { get; set; } = PREPROCESS_PATH_DEFAULT;
}

public enum Mode
{
    render = 0,
    train = 1,
    preprocess = 2
}

public enum Method
{
    ssim = 0,
    model = 1
}