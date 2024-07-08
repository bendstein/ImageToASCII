using ImageMagick;

namespace LibI2A.Common.Extensions;

public static class MagickExtensions
{
    public static double GetIntensity(this IMagickColor<ushort> color,
        (double H, double S, double V) HSVWeights)
    {
        //Convert to HSV
        (uint a, double h, double s, double v) hsv = Utils.ARGBToAHSV((
            Utils.ScaleUShort(color.A),
            Utils.ScaleUShort(color.R),
            Utils.ScaleUShort(color.G),
            Utils.ScaleUShort(color.B)));

        //Weight HSV components
        (uint a, double, double, double) weighted = (hsv.a, 
            double.Clamp(hsv.h * HSVWeights.H, 0, 1),
            double.Clamp(hsv.s * HSVWeights.S, 0, 1),
            double.Clamp(hsv.v * HSVWeights.V, 0, 1));

        //Convert back to rgb
        (uint a, uint r, uint g, uint b) rgb = Utils.AHSVToARGB(weighted);

        return Utils.GetIntensity(rgb);
    }
}