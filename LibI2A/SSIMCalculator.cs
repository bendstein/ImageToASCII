namespace LibI2A;

public class SSIMCalculator
{
    private readonly Options options;

    private static readonly (double H, double S, double V) HSV_WEIGHTS = (1, 1.8d, 1.1d);

    public SSIMCalculator(Action<Options>? configure = null)
    {
        Options options = new();
        configure?.Invoke(options);
        this.options = options;
    }

    public SSIMCalculator(Options options)
    {
        this.options = options;
    }

    public double Calculate(PixelImage image_a, PixelImage image_b)
    {
        //Calculate SSIM for subtile
        double CalculateSubtile(PixelImage image_a, PixelImage image_b)
        {
            //Generate an NxN circular gaussian matrix
            double[] gaussian = GenerateGaussian(
                Math.Max(image_a.Width, image_b.Width),
                Math.Max(image_a.Height, image_b.Height),
                options.GaussianStdDev
            );

            double W(int i) => i < gaussian.Length ? gaussian[i] : 0;

            List<double> local_ssims = [];

            //Compare each pixel. Apply weights to HSV components of the pixel for the calculation
            (List<double> a, List<double> b) intensities = (
                a: image_a.GetIntensities(HSV_WEIGHTS),
                b: image_b.GetIntensities(HSV_WEIGHTS)
            );

            //Get gaussian-weighted mean intensity of each image (luminance), scaled by gaussian
            (double a, double b) mean = (
                a: intensities.a.Count == 0 ? 0 : intensities.a.Select((i, ndx) => i * W(ndx)).Sum(),
                b: intensities.b.Count == 0 ? 0 : intensities.b.Select((i, ndx) => i * W(ndx)).Sum()
            );

            //Get gaussian-weighted standard deviation of the intensities of each image (contrast)
            (double a, double b) stddev = (
                a: intensities.a.Count <= 0 ? 0 : Math.Sqrt(intensities.a.Select((i, ndx) => W(ndx) * Math.Pow(i - mean.a, 2)).Sum()),
                b: intensities.b.Count <= 1 ? 0 : Math.Sqrt(intensities.b.Select((i, ndx) => W(ndx) * Math.Pow(i - mean.b, 2)).Sum())
            );

            //Get gaussian-weighted covariation of the intensities of each tile
            double covar = 0d;

            int min_count = Math.Min(intensities.a.Count, intensities.b.Count);

            if (min_count > 1)
            {
                for (int i = 0; i < min_count; i++)
                {
                    (double a, double b) intensity = (
                        a: i < intensities.a.Count ? intensities.a[i] : 0,
                        b: i < intensities.b.Count ? intensities.b[i] : 0
                    );

                    covar += W(i) * (intensity.a - mean.a) * (intensity.b - mean.b);
                }
            }

            //Constants to prevent instability around 0
            double c1 = options.Constants.K1;
            double c2 = options.Constants.K2;

            //Weight mean, std dev, and covar respectively
            double w1 = options.Weights.W1;
            double w2 = options.Weights.W2;
            double w3 = options.Weights.W3;

            //Calculate local SSIM
            double ssim = ((w1 * (2 * mean.a * mean.b)) + c1)
                * ((w3 * (2 * covar)) + c2)
                /
                (((w1 * (Math.Pow(mean.a, 2) + Math.Pow(mean.b, 2))) + c1)
                * ((w2 * (Math.Pow(stddev.a, 2) + Math.Pow(stddev.b, 2))) + c2));

            return ssim;
        }

        //Break tile into 2^subdivide subtiles, and calculate the SSIM for each, return the gaussian-weighted average
        int subtile_width_a = (int)Math.Ceiling(image_a.Width / Math.Pow(2, options.Subdivide));
        int subtile_height_a = (int)Math.Ceiling(image_a.Height / Math.Pow(2, options.Subdivide));
        int subtile_width_b = (int)Math.Ceiling(image_b.Width / Math.Pow(2, options.Subdivide));
        int subtile_height_b = (int)Math.Ceiling(image_b.Height / Math.Pow(2, options.Subdivide));

        List<PixelImage> subtiles_a = image_a.Tiles(subtile_width_a, subtile_height_a).ToList();
        List<PixelImage> subtiles_b = image_b.Tiles(subtile_width_b, subtile_height_b).ToList();

        //Generate an NxN circular gaussian matrix
        double[] gaussian = GenerateGaussian(
            Math.Max(subtile_width_a, subtile_width_b),
            Math.Max(subtile_height_a, subtile_height_b),
            options.GaussianStdDev
        );

        double W(int i) => i < gaussian.Length ? gaussian[i] : 0;

        int subtiles = Math.Max(subtiles_a.Count, subtiles_b.Count);

        double ssim_sum = 0d;

        for (int t = 0; t < subtiles; t++)
        {
            PixelImage? subtile_a = t < subtiles_a.Count ? subtiles_a[t] : null;
            PixelImage? subtile_b = t < subtiles_b.Count ? subtiles_b[t] : null;

            //Calculate SSIM for subtile, and weight by gaussian
            if (subtile_a != null && subtile_b != null)
            {
                double[] intensities_a = [.. image_a.GetIntensities()];
                double[] intensities_b = [.. image_b.GetIntensities()];

                ssim_sum += W(t) * CalculateSubtile(subtile_a, subtile_b);
            }
        }

        //Return average of gaussian weighted SSIMs
        return subtiles == 0 ? 0 : ssim_sum / subtiles;
    }

    /// <summary>
    /// Generate a w x h circular gaussian matrix with stddev
    /// </summary>
    /// <param name="w"></param>
    /// <param name="h"></param>
    /// <param name="stddev"></param>
    /// <returns></returns>
    private static double[] GenerateGaussian(int w, int h, double stddev)
    {
        double[] gaussian = new double[w * h];

        (double x, double y) center = ((w - 1) / 2d, (h - 1) / 2d);

        double sum = 0d;

        for (int i = 0; i < w; i++)
        {
            for (int j = 0; j < h; j++)
            {
                double value = 1d / (2 * Math.PI * Math.Pow(stddev, 2))
                        * Math.Exp((Math.Pow(i - center.x, 2) + Math.Pow(j - center.y, 2))
                            / (-2 * Math.Pow(stddev, 2)));

                sum += value;

                gaussian[(w * j) + i] = value;
            }
        }

        //Scale to 1
        for (int i = 0; i < gaussian.Length; i++)
        {
            gaussian[i] /= sum;
        }

        return gaussian;
    }

    public class Options
    {
        public int Subdivide { get; set; } = 1;

        public (double K1, double K2) Constants = (0.001d, 0.005d);

        public double GaussianStdDev { get; set; } = 1.5d;

        public (double W1, double W2, double W3) Weights = (1d, 2d, 1d);
    }
}