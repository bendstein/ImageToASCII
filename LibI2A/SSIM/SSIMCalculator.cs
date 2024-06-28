using System.Security.Cryptography.X509Certificates;

namespace LibI2A.SSIM;

public class SSIMCalculator : ISSIMCalculator
{
    private readonly ISSIMStore store;

    private readonly Options options;

    public SSIMCalculator(ISSIMStore store, Action<Options>? configure = null)
    {
        this.store = store;
        Options options = new();
        configure?.Invoke(options);
        this.options = options;
    }

    public SSIMCalculator(ISSIMStore store, Options options)
    {
        this.store = store;
        this.options = options;
    }

    public double Calculate(PixelImage image_a, PixelImage image_b)
    {
        //Calculate SSIM for subtile
        double CalculateSubtile(PixelImage image_a, PixelImage image_b)
        {
            double[] gaussian = GenerateGaussian(
                Math.Max(image_a.Width, image_b.Width),
                Math.Max(image_a.Height, image_b.Height),
                options.GaussianStdDev
            );

            double W(int i) => i < gaussian.Length ? gaussian[i] : 0;

            List<double> local_ssims = [];

            //Compare each pixel
            var intensities = (
                a: image_a.Intensities,
                b: image_b.Intensities
            );

            //Get gaussian-weighted mean intensity of each image (luminance), scaled by gaussian
            var mean = (
                a: intensities.a.Count == 0 ? 0 : 1d / intensities.a.Count * intensities.a
                    .Select((i, ndx) => i * W(ndx)).Sum(),
                b: intensities.b.Count == 0 ? 0 : 1d / intensities.b.Count * intensities.b
                    .Select((i, ndx) => i * W(ndx)).Sum()
            );

            //Get gaussian-weighted standard deviation of the intensities of each image (contrast)
            var stddev = (
                a: intensities.a.Count <= 1 ? 0 : 1d / (intensities.a.Count - 1)
                    * Math.Sqrt(intensities.a.Select((i, ndx) => Math.Pow(i - mean.a, 2) * W(ndx)).Sum()),
                b: intensities.b.Count <= 1 ? 0 : 1d / (intensities.b.Count - 1)
                    * Math.Sqrt(intensities.b.Select((i, ndx) => Math.Pow(i - mean.a, 2) * W(ndx)).Sum())
            );

            //Get gaussian-weighted covariation of the intensities of each tile
            var covar = 0d;

            var max_count = Math.Max(intensities.a.Count, intensities.b.Count);

            if (max_count > 1)
            {
                for (int i = 0; i < max_count; i++)
                {
                    var intensity = (
                        a: i < intensities.a.Count ? intensities.a[i] : 0,
                        b: i < intensities.b.Count ? intensities.b[i] : 0
                    );

                    covar += W(i) * (intensity.a - mean.a) * (intensity.b - mean.b);
                }

                covar /= Math.Max(intensities.a.Count, intensities.b.Count) - 1;
            }

            //Constants to prevent instability around 0
            var c1 = Math.Pow(Math.Pow(2, image_a.BitDepth) * options.Constants.K1, 2);
            var c2 = Math.Pow(Math.Pow(2, image_a.BitDepth) * options.Constants.K2, 2);

            //Calculate local SSIM
            var ssim = (((2 * mean.a * mean.b) + c1)
                * ((2 * covar) + c2))
                /
                (((Math.Pow(mean.a, 2) * Math.Pow(mean.b, 2)) + c1)
                * ((Math.Pow(stddev.a, 2) * Math.Pow(stddev.b, 2)) + c2));

            return ssim;
        }

        //Break tile into 2^subdivide subtiles, and calculate the SSIM for each, return the gaussian-weighted average
        int subtile_width_a = (int)Math.Ceiling(image_a.Width / Math.Pow(2, options.Subdivide));
        int subtile_height_a = (int)Math.Ceiling(image_a.Height / Math.Pow(2, options.Subdivide));
        int subtile_width_b = (int)Math.Ceiling(image_b.Width / Math.Pow(2, options.Subdivide));
        int subtile_height_b = (int)Math.Ceiling(image_b.Height / Math.Pow(2, options.Subdivide));

        var subtiles_a = image_a.Tiles(subtile_width_a, subtile_height_a).ToList();
        var subtiles_b = image_b.Tiles(subtile_width_b, subtile_height_b).ToList();

        double[] gaussian = GenerateGaussian(
            Math.Max(subtile_width_a, subtile_width_b),
            Math.Max(subtile_height_a, subtile_height_b),
            options.GaussianStdDev
        );

        double W(int i) => i < gaussian.Length ? gaussian[i] : 0;

        var subtiles = Math.Max(subtiles_a.Count, subtiles_b.Count);

        var ssim_sum = 0d;

        for (int t = 0; t < subtiles; t++)
        {
            var subtile_a = t < subtiles_a.Count ? subtiles_a[t] : null;
            var subtile_b = t < subtiles_b.Count ? subtiles_b[t] : null;

            //Calculate SSIM for subtile, and weight by gaussian
            if (subtile_a != null && subtile_b != null)
            {
                var intensities_a = image_a.Intensities.ToArray();
                var intensities_b = image_b.Intensities.ToArray();

                ssim_sum += store.GetOrCalculateAndStore(intensities_a, intensities_b,
                    () => W(t) * CalculateSubtile(subtile_a, subtile_b));
            }
        }

        //Return average of gaussian weighted SSIMs
        return subtiles == 0? 0 : ssim_sum / subtiles;
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
        var gaussian = new double[w * h];

        (double x, double y) center = ((double)(w % 2 == 0? w + 1 : w) / 2 - 1, (double)(h % 2 == 0 ? h + 1 : w) / 2 - 1);

        double max = 0.001;

        for (int i = 0; i < w; i++)
        {
            for (int j = 0; j < h; j++)
            {
                var opposite = (w * (h - 1 - j)) + (w - 1 - i);

                double value;

                if (gaussian[opposite] == 0)
                {
                    value = 1d / (2 * Math.PI * Math.Pow(stddev, 2))
                        * Math.Exp((Math.Pow(i - center.x, 2) + Math.Pow(j - center.y, 2))
                            / (-2 * Math.Pow(stddev, 2)));
                }
                else
                {
                    value = gaussian[opposite];
                }         

                max = Math.Max(max, value);

                gaussian[(w * j) + i] = value;
            }
        }

        //Scale to 1
        for(int i = 0; i < gaussian.Length; i++)
            gaussian[i] /= max;

        return gaussian;
    }

    public class Options
    {
        public int Subdivide { get; set; } = 1;

        public (double K1, double K2) Constants = (0.01d, 0.03d);

        public double GaussianStdDev { get; set; } = 1.5d;
    }
}