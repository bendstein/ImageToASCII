namespace LibI2A.SSIM;

public interface ISSIMCalculator
{
    double Calculate(PixelImage image_a, PixelImage image_b);
}
