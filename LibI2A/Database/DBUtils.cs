using LibI2A.Converter;
using Microsoft.Data.Sqlite;
using System.Diagnostics.CodeAnalysis;

namespace LibI2A.Database;

public static class DBUtils
{
    public static void Initialize(SqliteConnection connection)
    {
        using var comm = connection.CreateCommand();

        comm.CommandText = $@"
            create table GlyphSSIMData (
                symbol nvarchar primary key,
                meanluminance double,
                stddevluminance double,
                luminances text
            );
        ";

        comm.ExecuteNonQuery();

        comm.CommandText = $@"
            create table SSIMMemo (
                values_joined nvarchar primary key,
                symbol varchar
            );
        ";

        comm.ExecuteNonQuery();
    }

    public static void WriteGlyphData(SqliteConnection connection, int font_size, string font_name, IEnumerable<Glyph> glyphs)
    {
        foreach(var glyph in glyphs)
        {
            using var comm = connection.CreateCommand();

            comm.CommandText = $@"
                insert into 
                    GlyphSSIMData
                    (symbol, meanluminance, stddevluminance, luminances)
                values
                    (@key, @meanlum, @stddevlum, @lums)
                on conflict(symbol) do update set
                    meanluminance = excluded.meanluminance,
                    stddevluminance = excluded.stddevluminance,
                    luminances = excluded.luminances
                where
                    symbol = @key;
            ";

            comm.Parameters.AddWithValue("@key", $"{font_size};{font_name};{glyph.Symbol}");
            comm.Parameters.AddWithValue("@meanlum", glyph.SSIM?.MeanLuminance?? 0d);
            comm.Parameters.AddWithValue("@stddevlum", glyph.SSIM?.StdDevLuminance?? 0d);
            comm.Parameters.AddWithValue("@lums", glyph.SSIM?.Luminances?.Select(l => $"{l ?? glyph.SSIM?.MeanLuminance ?? 0d}")?.AggregateOrDefault((a, b) => $"{a},{b}", string.Empty)?? string.Empty);

            int affected = comm.ExecuteNonQuery();
        }
    }

    public static Glyph[] ReadGlyphData(SqliteConnection connection, int font_size, string font_name, Glyph[] glyphs)
    {
        var rv = new Glyph[glyphs.Length];

        for (int i = 0; i < glyphs.Length; i++)
        {
            using var comm = connection.CreateCommand();

            var glyph = glyphs[i];

            comm.CommandText = $@"
                select
                    meanluminance, 
                    stddevluminance, 
                    luminances
                from
                    GlyphSSIMData
                where
                    symbol = @key;
            ";

            comm.Parameters.AddWithValue("@key", $"{font_size};{font_name};{glyph.Symbol}");

            using var reader = comm.ExecuteReader();
            if(reader.Read())
            {
                glyph.SSIM = new SSIM()
                {
                    MeanLuminance = reader.GetFieldValue<double>(0),
                    StdDevLuminance = reader.GetFieldValue<double>(1),
                    Luminances = reader.GetFieldValue<string>(2).Split(',')
                        .Select(l => double.TryParse(l, out var _l)? _l : (double?)null)
                        .ToArray()
                };
            }

            rv[i] = glyph;
        }

        return rv;
    }

    public static bool TryGetMemoizedGlpyh(SqliteConnection connection,
        double?[] luminances,
        (int w, int h) window,
        int bit_depth,
        (double luminance, double contrast, double structure) coeffs,
        (double luminance, double contrast, double structure) weights,
        [NotNullWhen(true)] out string? glyph)
    {
        glyph = null;

        string key = $@"{luminances.Select(d => d?.ToString()?? string.Empty).AggregateOrDefault((a, b) => $"{a},{b}", string.Empty)};{window.w};{window.h};{bit_depth};{coeffs.luminance};{coeffs.contrast};{coeffs.structure};{weights.luminance};{weights.contrast};{weights.structure}";

        using var comm = connection.CreateCommand();
        comm.CommandText = $@"
            select
                symbol
            from
                SSIMMemo
            where
                values_joined = @key;
        ";

        comm.Parameters.AddWithValue("@key", key);

        using var reader = comm.ExecuteReader();

        if (reader.Read())
            glyph = reader.GetFieldValue<string>(0);

        return glyph != null;
    }

    public static void WriteMemoizedGlyph(SqliteConnection connection,
        double?[] luminances,
        (int w, int h) window,
        int bit_depth,
        (double luminance, double contrast, double structure) coeffs,
        (double luminance, double contrast, double structure) weights,
        string glyph)
    {
        string key = $@"{luminances.Select(d => d?.ToString() ?? string.Empty).AggregateOrDefault((a, b) => $"{a},{b}", string.Empty)};{window.w};{window.h};{bit_depth};{coeffs.luminance};{coeffs.contrast};{coeffs.structure};{weights.luminance};{weights.contrast};{weights.structure}";

        using var comm = connection.CreateCommand();
        comm.CommandText = $@"
        insert into 
            SSIMMemo
            (values_joined, symbol)
        values
            (@key, @glyph)
        on conflict(values_joined) do update set
            symbol = excluded.symbol
        where
            values_joined = @key;
        ";

        comm.Parameters.AddWithValue("@key", key);
        comm.Parameters.AddWithValue("@glyph", glyph);

        int affected = comm.ExecuteNonQuery();
    }

    public static void FlushMemoizedGlyphs(SqliteConnection connection)
    {
        using var comm = connection.CreateCommand();
        comm.CommandText = "delete from SSIMMemo;";
        int affected = comm.ExecuteNonQuery();
    }
}