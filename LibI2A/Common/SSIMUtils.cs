using ImageMagick;
using LibI2A.Common.Extensions;
using LibI2A.Converter;

namespace LibI2A.Common;

public static class SSIMUtils
{
    public static IEnumerable<(IEnumerable<IPixel<ushort>?> tile, SSIM values)> CalculateSSIMValues(IMagickImage<ushort> image,
        Stack<IMagickImage<ushort>> layers_below, int tile_width, int tile_height)
    {
        /*Break image into tile_width x tile_height tiles*/

        int pixels_per_tile = tile_width * tile_height;
        int effective_length = (image.Width + (tile_width - (image.Width % tile_width))) * (image.Height + (tile_height - (image.Height % tile_height)));
        int tiles_per_image = effective_length / pixels_per_tile;

        /* 6x5 image, 3x3 tiles; Pixel (3, 1): Window: 1, Index: 3, Linear index: 11
         *  . . . . .   0 0 0 1 1   0 1 2 0 1   0 1 2 9 A
         *  . . . * .   0 0 0 1 1   3 4 5 3 4   3 4 5 B C
         *  . . . . .   0 0 0 1 1   6 7 8 6 7   6 7 8 D E
         *  . . . . .   2 2 2 3 3   0 1 2 0 1   F G H L M
         *  . . . . .   2 2 2 3 3   3 4 5 3 4   I J K N O
         * Linear index: 
         * Window: ((1 / 3) * ((5 + (3 - 1)) / 3)) + (3 / 3) = 1
         * Index: (3 % 3) + ((1 % 3) * 3)
         *  
         *  For a WxH image with wxh tiles, a pixel (i, j):
         *  Window: ((j / h) * ((W + (w - 1)) / w)) + (i / w)
         *  Index: (i % w) + ((j % h) * w)
         *  Linear Index: (Window * w * h) + Index
         */

        var pixels = new IPixel<ushort>?[effective_length];

        int GetPixelIndex(IPixel<ushort> pixel)
        {
            //While tile should this pixel be in
            var tile = ((pixel.Y / tile_height) * ((image.Width + (tile_width - 1)) / tile_width)) + (pixel.X / tile_width);

            //What is the index of this pixel in the tile
            var index_in_tile = (pixel.X % tile_width) + ((pixel.Y % tile_height) * tile_width);

            //Combine the two indices to get the index in a linear array
            var linear_index = (tile * pixels_per_tile) + index_in_tile;
            return linear_index;
        }

        foreach (var pixel in image.GetPixels())
        {
            var index = GetPixelIndex(pixel);
            pixels[index] = pixel;
        }

        var tiles = new (IEnumerable<IPixel<ushort>?> tile, SSIM values)[tiles_per_image];

        //Loop over each tile
        for(int t = 0; t < tiles_per_image; t++)
        {
            int tile_offset = t * pixels_per_tile;
            var tile = pixels[tile_offset..(tile_offset + pixels_per_tile)];

            //Calculate values for tile
            double mean_luminance = 0d;
            double?[] luminances = new double?[pixels_per_tile];
            int counter = 0;

            //Loop over pixels, skipping empty ones (from tile extending past edge of image)
            for(int i = 0; i < tile.Length; i++)
            {
                var pixel = tile[i];
                var color = pixel?.ToColor();
                if (pixel == null || color == null)
                    continue;

                //If color has transparency, and there are any previous layers, coalesce
                (uint a, uint r, uint g, uint b) argb;

                if(color.A < ushort.MaxValue && layers_below.Any())
                {
                    argb = InternalUtils.CoalescePixel(pixel, layers_below);
                }
                else
                {
                    argb = (
                        InternalUtils.ScaleUShort(color.A),
                        InternalUtils.ScaleUShort(color.R),
                        InternalUtils.ScaleUShort(color.G),
                        InternalUtils.ScaleUShort(color.B));
                }

                //Increment counter for mean when pixel is present
                counter++;

                //Get luminance for color
                var luminance = InternalUtils.GetLuminance(argb);

                //Record value and increment mean luminance
                mean_luminance += luminance;
                luminances[i] = luminance;
            }

            //Get mean luminance by dividing sum by number of pixels in window
            mean_luminance /= Math.Max(1, counter);

            //Calculate contrast as stdev of luminances
            double stdev_luminance = 0d;

            foreach(var luminance in luminances.Where(l => l != null))
                stdev_luminance += Math.Pow(luminance!.Value - mean_luminance, 2);
            
            stdev_luminance /= Math.Max(1, counter - 1);
            stdev_luminance = Math.Sqrt(stdev_luminance);

            //Record data for tile
            tiles[t] = (tile, new SSIM()
            {
                Luminances = luminances,
                MeanLuminance = mean_luminance,
                StdDevLuminance = stdev_luminance
            });
        }

        return tiles;
    }

    public static Glyph[] CalculateGlyphSSIMValues(Glyph[] glyphs, int font_point_size, string font_face)
    {
        var with_values = new Glyph[glyphs.Length];

        for(int i = 0; i < glyphs.Length; i++)
        {
            var glyph = glyphs[i];

            using MagickImage glyph_image = new(MagickColors.Black, font_point_size, font_point_size);

            new Drawables()
                .FontPointSize(font_point_size)
                .Font(font_face, FontStyleType.Normal, FontWeight.Thin, FontStretch.Normal)
                .StrokeColor(MagickColors.White)
                .FillColor(MagickColors.White)
                .Gravity(Gravity.Center)
                .Text(0, 0, glyph.Symbol.ToString())
                .Draw(glyph_image);

            (_, var ssim) = CalculateSSIMValues(glyph_image, new(), font_point_size, font_point_size).First();
            with_values[i] = glyph.WithSSIM(ssim);
        }

        return with_values;
    }

    public static SSIMComparison CompareSSIMs(SSIM a, SSIM b,
        (double luminance, double contrast, double structure) coeffs,
        (double luminance, double contrast, double structure) weights,
        int bit_depth)
    {
        //Constants to add to small numbers
        (double luminance, double contrast, double structure) small_constants = 
            (Math.Pow(coeffs.luminance * bit_depth, 2), Math.Pow(coeffs.luminance * bit_depth, 2), Math.Pow(coeffs.luminance * bit_depth, 2));

        int scaled = b.Luminances.Length % a.Luminances.Length == 0?
            b.Luminances.Length
            : a.Luminances.Length * b.Luminances.Length;

        //Scale luminances for a and b to be the same length
        var a_scaled = Utils.StretchArray(a.Luminances, scaled);
        var b_scaled = Utils.StretchArray(b.Luminances, scaled);

        //Calculate correlation coefficient between the two images
        double correlation = 0d;

        for(int i = 0; i < scaled; i++)
        {
            double la = a_scaled.Length > i? (a_scaled[i] ?? a.MeanLuminance) : a.MeanLuminance;
            double lb = b_scaled.Length > i? (b_scaled[i] ?? b.MeanLuminance) : b.MeanLuminance;
            correlation += (la - a.MeanLuminance) * (lb - b.MeanLuminance);
        }

        double variation_a = Math.Pow(a.StdDevLuminance, 2) * 2;
        double variation_b = Math.Pow(b.StdDevLuminance, 2) * 2;

        correlation /= Math.Max(1, Math.Sqrt(variation_a * variation_b));

        double luminance = ((2 * a.MeanLuminance * b.MeanLuminance) + small_constants.luminance) / ((Math.Pow(a.MeanLuminance, 2) + Math.Pow(b.MeanLuminance, 2)) + small_constants.luminance);

        double contrast = ((Math.Pow(a.StdDevLuminance, 2) + Math.Pow(b.StdDevLuminance, 2)) + small_constants.luminance) / ((Math.Pow(a.StdDevLuminance, 2) + Math.Pow(b.StdDevLuminance, 2)) + small_constants.contrast);

        double structure = (Math.Pow(correlation, 2) + small_constants.structure) / ((a.StdDevLuminance * b.StdDevLuminance) + small_constants.structure);

        return new()
        {
            Luminance = Math.Pow(luminance, weights.luminance),
            Contrast = Math.Pow(contrast, weights.contrast),
            Structure = Math.Pow(structure, weights.structure)
        };
    }
}