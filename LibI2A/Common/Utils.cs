namespace LibI2A.Common;

public static class Utils
{
    public static string[] ReadGlyphsFile(Stream stream)
    {
        using var sr = new StreamReader(stream);

        var glyphs = sr.ReadToEnd().Split(Environment.NewLine)
            .Where(line => line.Length > 0)
            //.Select(line => line.Trim())
            .Distinct()
            .ToArray();

        return glyphs;
    }
}