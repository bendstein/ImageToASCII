using Driver;
using ImageMagick;
using LibI2A;
using LibI2A.Common;
using LibI2A.Converters;
using LibI2A.Database;
using Microsoft.Data.Sqlite;
using Pastel;
using System.Linq;
using System.Text;

try
{
    //Initialize database
    const string DB = "i2a.db";
    bool init = File.Exists(DB);

    using var connection = new SqliteConnection($"Data Source={DB}");

    connection.Open();

    if(!init)
        DBUtils.Initialize(connection);

    var config = Config.ParseArgs(args);

    //If specified, flush pixel-glyph memo
    if (config.FlushCache)
        DBUtils.FlushMemoizedGlyphs(connection);

    //Make sure the image exists
    if (!File.Exists(config.Path))
        throw new Exception($"Image not found at '{config.Path}'.");

    //Make sure the glyphs file exists
    if(!File.Exists(config.Glyphs))
        throw new Exception($"Glyphs file not found at '{config.Glyphs}'.");

    //Set VT if specified
    if(config.SetVT)
    {
        try
        {
            ConsoleUtils.SetVTMode(out _);
        }
        catch { }
    }

    //Read glyphs file
    Glyph[] glyphs;

    using(var fs = new FileStream(config.Glyphs, FileMode.Open, FileAccess.Read))
        glyphs = Utils.ReadGlyphsFile(fs);

    //Read glyph SSIM data
    glyphs = DBUtils.ReadGlyphData(connection, config.FontSize, config.FontFace, glyphs);

    //If any glyphs missing SSIM data (or if explicitly configured in the args), calculate
    var glyphs_wo_ssim = config.RecalculateGlyphs? glyphs : glyphs.Where(s => !s.SSIM.HasValue);

    if(glyphs_wo_ssim.Any())
    {
        var calculated = SSIMUtils.CalculateGlyphSSIMValues(glyphs_wo_ssim.ToArray(), config.FontSize, config.FontFace);
        glyphs = (config.RecalculateGlyphs ? calculated : glyphs.Where(s => s.SSIM.HasValue).Union(calculated)).ToArray();

        //Write updated glyph ssim data
        DBUtils.WriteGlyphData(connection, config.FontSize, config.FontFace, calculated);

        ////Write updated glyphs to file
        //using(var fs = new FileStream(config.Glyphs, FileMode.Open, FileAccess.Write))
        //{
        //    fs.Position = 0;
        //    Utils.WriteGlyphsFile(glyphs, fs);
        //    fs.SetLength(fs.Position);
        //}
    }

    if (glyphs.Length == 0)
        throw new Exception($"No glyphs were present.");

    //Convert image to string
    IImageToASCIIConverter converter = new SSIMConverter(glyphs, connection, new()
    {
        Window = new(config.TileSize, config.TileSize),
        Precision = 2,
        coeffs = config.SSIMCoeffs,
        weights = config.SSIMWeights,
        Clamp = config.Clamp,
        AllowANSIEscapes = true,
        Repeat = config.Repeat,
        WriteAll = config.WriteAll
    });

    using var stream = Console.OpenStandardOutput();

    converter.ConvertImage(config.Path, stream,
        (output, color) =>
        {
            //Repeat glyph {CharWidth} times
            StringBuilder sb = new();

            for (int i = 0; i < config.CharWidth; i++)
                sb.Append(output);

            var rv = sb.ToString();

            //Color the glyph if specified
            if (!config.NoColor)
                rv = rv.Pastel(System.Drawing.Color.FromArgb(
                    (int)(color >> 24) & 0xFF,
                    (int)(color >> 16) & 0xFF,
                    (int)(color >> 8) & 0xFF,
                   (int)color >> 0 & 0xFF));

            return rv;
        },
        (_, _) => { });

    //string image_as_ascii = converter.ConvertImage(config.Path);

    ////Write image to console
    //Console.WriteLine(image_as_ascii);
}
catch(Exception e)
{
    Console.Error.WriteLine($"An error occurred: {e.Message}\r\n{e.StackTrace}");
}