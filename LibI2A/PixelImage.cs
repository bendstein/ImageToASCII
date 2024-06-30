using ImageMagick;
using LibI2A.Common.Extensions;
using System.Data;

namespace LibI2A;

public class PixelImage
{
    public IMagickImage<ushort> OriginalImage { get; set; }

    public IMagickColor<ushort>?[] Pixels { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int BitDepth => OriginalImage.DetermineBitDepth();

    public List<double> GetIntensities((double H, double S, double V)? HSVWeights = null)
        => Pixels.Select(p => p?.GetLuminance(HSVWeights ?? (1, 1, 1)) ?? 0).ToList();

    public PixelImage(IMagickImage<ushort> originalImage)
    {
        OriginalImage = originalImage;
        Pixels = OriginalImage.GetPixels()
            .Select(p => p.ToColor())
            .ToArray();
        Width = OriginalImage.Width;
        Height = OriginalImage.Height;
    }

    public PixelImage(IMagickImage<ushort> originalImage, IMagickColor<ushort>?[] pixels,
        int width, int height)
    {
        OriginalImage = originalImage;
        Pixels = pixels;
        Width = width;
        Height = height;
    }

    public IEnumerable<PixelImage> Tiles(int width, int height)
    {
        //Unflatten pixel array, pad to fit multiples of tiles
        int padded_image_width = Width + width - (Width % width == 0? width : Width % width);
        int padded_image_height = Height + height - (Height % height == 0? height : Height % height);

        var pixels_2d = new IMagickColor<ushort>?[padded_image_width, padded_image_height];

        for (int j = 0; j < Height; j++)
        {
            for (int i = 0; i < Width; i++)
            {
                var pixel = Pixels[i + (j * Width)];
                pixels_2d[i, j] = pixel;
            }
        }
        

        //Evaluate one row at a time
        for(int k = 0; k < padded_image_height; )
        {
            List<List<IMagickColor<ushort>?>> row = [];

            for(int j = 0; j < height; j++, k++)
            {
                for(int i = 0; i < padded_image_width; i++)
                {
                    if (i / width == row.Count)
                        row.Add([]);
                    var tile = row[i / width];
                    tile.Add(pixels_2d[i, k]);
                }
            }

            foreach(var tile in row)
            {
                yield return new PixelImage(OriginalImage,
                    [..tile], width, height);
            }
        }

        yield break;
    }
}