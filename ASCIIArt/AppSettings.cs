using Spectre.Console.Cli;
using System.ComponentModel;

namespace I2A;
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
                    return int.TryParse(s, out int i) && i >= 0 ? (int?)i : null;
                }).ToArray();

            return (split.Length > 0 ? split[0] : null, split.Length > 1 ? split[1] : null);
        }
        set => ClampString = $"{(value.w.HasValue ? value.w.ToString() : "")},{(value.h.HasValue ? value.h.ToString() : "")}";
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

    public string MethodString { get; set; } = string.Empty;

    [CommandOption("--method")]
    [DefaultValue(METHOD_DEFAULT)]
    [Description("The way to determine similarity between a tile and a glyph in render mode. "
        + "ssim: Use the SSIM to determine similarity; model: Use a trained model to determine similarity.")]
    public Method Method
    {
        get => Enum.TryParse(MethodString, out Method m) ? m : Method.ssim;
        set => MethodString = value.ToString();
    }

    [CommandOption("--preprocess")]
    [DefaultValue(PREPROCESS_PATH_DEFAULT)]
    [Description("Pull preprocessed data from this file/directory when in training mode. Output results to this file in preprocessing mode.")]
    public string PreprocessPath { get; set; } = PREPROCESS_PATH_DEFAULT;

    [CommandOption("--shuffle")]
    [DefaultValue(false)]
    [Description("Shuffle preprocessed input.")]
    public bool Shuffle { get; set; } = false;

    [CommandOption("--learning-rate")]
    [DefaultValue(0.01d)]
    [Description("The rate at which the model learns.")]
    public double LearningRate { get; set; } = 0.01d;

    [CommandOption("--learning-rate-decay")]
    [DefaultValue(0.01d)]
    [Description("The continuously compounded rate at which learning rate decreases with each epoch.")]
    public double LearningDecay { get; set; } = 0.01d;

    [CommandOption("--alpha")]
    [DefaultValue(0d)]
    [Description("The leak rate of leaky ReLU.")]
    public double ReLUAlpha { get; set; } = 0d;

    [CommandOption("--hidden-layers")]
    [DefaultValue(0)]
    [Description("The number of inner layers in the neural net.")]
    public int HiddenLayers { get; set; } = 0;

    [CommandOption("--hidden-neurons")]
    [DefaultValue(0)]
    [Description("The number of neurons each hidden layer will have.")]
    public int HiddenNeurons { get; set; } = 0;

    [CommandOption("--batch-size")]
    [DefaultValue(64)]
    [Description("The size of the mini-batches to train the neural net on.")]
    public int BatchSize { get; set; } = 64;
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