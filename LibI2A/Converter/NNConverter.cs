using System.Diagnostics.CodeAnalysis;
using System.Text;
using ImageMagick;
using MathNet.Numerics.LinearAlgebra.Double;

namespace LibI2A.Converter;

public class NNConverter : IImageToASCIIConverter
{
    private const double 
        EPS = 0.00001d,
        GRADIENT_MAX = 2d,
        GRADIENT_MIN = -2d;

    private readonly Model model;

    private readonly Options options;

    public NNConverter(Model model, Action<Options>? configure = null)
    {
        this.model = model;
        Options options = new();
        configure?.Invoke(options);
        this.options = options;
    }

    public NNConverter(Model model, Options options)
    {
        this.model = model;
        this.options = options;
    }

    public IEnumerable<(string glyph, uint? color)> ConvertImage(Stream input)
    {
        using var image_collection = new MagickImageCollection(input);
        image_collection.Coalesce();
        var image = image_collection.First();

        //Break images into windows
        var pixel_image = new PixelImage(image);

        int width = (int)Math.Ceiling((double)image.Width / options.FontSize);
        int height = (int)Math.Ceiling((double)image.Height / options.FontSize);

        int n = 0;

        foreach (var tile in pixel_image.Tiles(options.FontSize, options.FontSize))
        {
            if (n > 0 && (n % width) == 0)
                yield return (Environment.NewLine, null);

            //Get average color of tile
            var colors = tile.Pixels
                .Where(color => color != null)
                .Select(color => InternalUtils.ARGBToAHSV((
                    InternalUtils.ScaleUShort(color!.A),
                    InternalUtils.ScaleUShort(color.R),
                    InternalUtils.ScaleUShort(color.G),
                    InternalUtils.ScaleUShort(color.B))))
                .ToList();

            uint combined = 0;

            if (colors.Count > 0)
            {
                var sums = colors.AggregateOrDefault<(uint a, double h, double s, double v)>((a, b) => (a.a + b.a, a.h + b.h, a.s + b.s, a.v + b.v), (0, 0, 0, 0));
                var avgs = ((uint)long.Clamp(sums.a / colors.Count, uint.MinValue, uint.MaxValue),
                    sums.h / colors.Count,
                    sums.s / colors.Count,
                    sums.v / colors.Count);
                combined = InternalUtils.ToUInt(InternalUtils.AHSVToARGB(avgs));
            }

            //Predict the glyph that is most likely
            string glyph = PredictGlyph(model, tile);

            yield return (glyph, options.NoColor ? null : combined);
            n++;
        }

        yield break;
    }

    private string PredictGlyph(Model model, PixelImage tile)
    {
        var neurons = DenseVector.OfArray([.. tile.GetIntensities()]);

        //For each layer, propagating forward
        for (int l = 0; l < model.Weights.Length; l++)
        {
            var layer = model.Weights[l];
            var bias = model.Biases[l];

            //Take dot product of weights and neurons, and add bias to get activation inputs
            var product = (layer * neurons) + bias;

            if (l == model.Weights.Length - 1)
            {
                //Apply output activation function (softmax)
                product = NormalizedSoftmax(product);
            }
            else
            {
                //Apply activation function (Leaky ReLU)
                product.MapInplace(value => (value < 0 ? model.Alpha : 1) * value);
            }

            //Push values to previous and use for next layer
            neurons = product;
        }

        //Select glyph with highest probability
        return neurons.Select((n, ndx) => (n, glyph: model.Glyphs[ndx]))
            .MaxBy(pair => pair.n)
            .glyph;
    }

    public static bool Train(ModelInitParams model_init_params, TrainingSet training_set, Action<string, bool> log,
        [NotNullWhen(true)] out Model? model, [NotNullWhen(false)] out string? error)
        => Train(new Model(model_init_params), training_set, log, out model, out error);

    public static bool Train(Model model_initial, TrainingSet training_set, Action<string, bool> log,
        [NotNullWhen(true)] out Model? model, [NotNullWhen(false)] out string? error)
    {
        void Log(string message, bool is_error = false)
        {
            log(message, is_error);
        }

        bool Error(string message, out string error)
        {
            Log(message, true);
            error = message;
            return false;
        }

        model = null;
        error = null;

        if (model_initial.Glyphs.Length == 0)
            return Error("No glyphs were provided.", out error);

        //Learning rate w/ exponential decay
        var learning_rate = training_set.LearningRate * Math.Exp(-training_set.LearningDecay * training_set.Epoch);
        var batch_size = training_set.Input.Count();

        if (batch_size == 0)
            return Error("Cannot train on 0 examples.", out error);

        var total_loss = 0d;

        var batch_activations = new List<DenseVector[]?>();
        foreach (var _ in training_set.Input)
            batch_activations.Add(null);

        SemaphoreSlim threads = new(training_set.Threads, training_set.Threads);

        //Run examples through the neural net in parallel
        List<Thread> forward_propagation_threads = training_set.Input.Select((input, input_index) => new Thread(() =>
        {
            try
            {
                var neurons = DenseVector.OfArray(input.Intensities);

                var activations = new DenseVector[model_initial.Weights.Length + 1];
                activations[0] = neurons;

                //For each layer, propagating forward
                for (int l = 0; l < model_initial.Weights.Length; l++)
                {
                    var layer = model_initial.Weights[l];
                    var bias = model_initial.Biases[l];

                    //Take dot product of weights and neurons, and add bias to get activation inputs
                    var product = (layer * neurons) + bias;

                    if (l == model_initial.Weights.Length - 1)
                    {
                        //Apply output activation function (softmax)
                        product = NormalizedSoftmax(product);
                    }
                    else
                    {
                        //Apply activation function (Leaky ReLU)
                        product.MapInplace(value => (value < 0? model_initial.Alpha : 1) * value);
                    }

                    //Push values to previous and use for next layer
                    neurons = product;
                    activations[l + 1] = neurons;
                }

                var output = activations[^1];

                var ssims = DenseVector.OfArray(input.NormalizedSSIMs);

                //Calculate error using cross entropy loss
                var loss = ssims * output.Map(value => Math.Log(Math.Max(EPS, value)));

                lock(batch_activations)
                {
                    //Accumulate loss
                    total_loss -= loss;

                    //Record activations for back propagation
                    batch_activations[input_index] = activations;
                }
            }
            finally
            {
                threads.Release();
            }
        })).ToList();

        //Run in parallel
        foreach(var thread in forward_propagation_threads)
        {
            threads.Wait();
            thread.Start();
        }

        //Wait for all threads to finish
        foreach (var thread in forward_propagation_threads)
            thread.Join();

        //Get average loss across batch
        var avg_loss = total_loss / batch_size;

        Log($"Batch loss: {avg_loss}");

        var batch_gradients = new List<(DenseMatrix[]? weights, DenseVector[]? biases)>();
        foreach (var _ in training_set.Input)
            batch_gradients.Add((null, null));

        //Backpropagate
        List<Thread> backward_propagation_threads = training_set.Input.Select((input, input_index) => new Thread(() =>
        {
            try
            {
                //Get corresponding activations
                var activations = batch_activations[input_index];

                if(activations != null)
                {
                    var ssims = DenseVector.OfArray(input.NormalizedSSIMs);

                    var all_gradients = new DenseMatrix[model_initial.Weights.Length];
                    var all_errors = new DenseVector[model_initial.Weights.Length];
                    var all_biases = new DenseVector[model_initial.Weights.Length];

                    for (int l = all_gradients.Length - 1; l >= 0; l--)
                    {
                        //Calculate the partial derivative of loss wrt to the weights of layer l
                        var weights = model_initial.Weights[l];
                        var neurons = activations[l + 1];

                        var layer_gradients = new DenseVector[weights.RowCount];
                        var layer_errors = new DenseVector(weights.RowCount);
                        var layer_bias = new DenseVector(weights.RowCount);

                        //For each output neuron
                        for (int i = 0; i < weights.RowCount; i++)
                        {
                            var row_gradients = DenseVector.OfArray(new double[weights.ColumnCount]);
                            double row_error = 0d;
                            var weights_row = weights.Row(i);

                            //Last layer
                            if(l == all_gradients.Length - 1)
                            {
                                //Gradient of cross-entropy loss wrt softmax activation simplifies to predicted - expected
                                row_error = neurons[i] - ssims[i];
                            }
                            //Inner layer
                            else
                            {
                                //Get weights and error terms for next layer
                                var next_weights = model_initial.Weights[l + 1];
                                var next_errors = all_errors[l + 1];

                                //Using chain rule, error term is the transpose of the next layer's weights times it's error term,
                                //times the derivative of ReLU
                                row_error = next_weights.TransposeThisAndMultiply(next_errors)
                                    * neurons.Map(n => n <= 0? model_initial.Alpha : 1);
                            }

                            layer_errors[i] = row_error;

                            //Grad of loss wrt bias is just error
                            layer_bias[i] = double.Clamp(row_error, GRADIENT_MIN, GRADIENT_MAX);

                            //Grad of loss wrt weights is error * prev activations
                            layer_gradients[i] = (DenseVector)(row_error * activations[l]).Map(n => double.Clamp(n, GRADIENT_MIN, GRADIENT_MAX));
                        }

                        all_gradients[l] = DenseMatrix.OfRowVectors(layer_gradients);
                        all_errors[l] = layer_errors;
                        all_biases[l] = layer_bias;
                    }

                    lock(batch_gradients)
                    {
                        batch_gradients[input_index] = (all_gradients, all_biases);
                    }
                }
            }
            finally
            {
                threads.Release();
            }
        })).ToList();

        //Run in parallel
        foreach (var thread in backward_propagation_threads)
        {
            threads.Wait();
            thread.Start();
        }

        //Wait for all threads to finish
        foreach (var thread in backward_propagation_threads)
            thread.Join();

        //Average gradients across all trials
        var nn_gradients = batch_gradients.Where(g => g.weights != null && g.biases != null).Select(g => (weights: g.weights!, biases: g.biases!)).ToList();
        var first_trial_gradients = nn_gradients.First();
        var average_gradients = new DenseMatrix[model_initial.Weights.Length];
        var average_bias_gradients = new DenseVector[model_initial.Weights.Length];

        for (int l = 0; l < model_initial.Weights.Length; l++)
        {
            //Sum gradients from each trial
            var sum = new DenseMatrix(first_trial_gradients.weights[l].RowCount, first_trial_gradients.weights[l].ColumnCount);
            var bias_sum = new DenseVector(first_trial_gradients.biases[l].Count);

            foreach (var gradient in nn_gradients)
            {
                sum += gradient.weights[l];
                bias_sum += gradient.biases[l];
            }

            //Divide sum by count to get average
            sum = (DenseMatrix)(sum / nn_gradients.Count);
            bias_sum /= nn_gradients.Count;
            average_gradients[l] = sum;
            average_bias_gradients[l] = bias_sum;
        }

        model = model_initial.Clone();

        //Update weights and biases
        for (int l = average_gradients.Length - 1; l >= 0; l--)
        {
            var gradient = average_gradients[l];
            var bias_gradient = average_bias_gradients[l];
            var weights = model.Weights[l];
            var biases = model.Biases[l];

            for (int i = 0; i < weights.RowCount; i++)
            {
                //Update bias, using sum of gradients as gradient
                biases[i] -= learning_rate * bias_gradient[i];

                //Update weight
                for (int j = 0; j < weights.ColumnCount; j++)
                    weights[i, j] -= learning_rate * gradient[i, j];
            }
        }

        return true;
    }

    private static void InitializeWeightsHeNormal(double[,] weights)
    {
        var fan_in = weights.GetLength(1);
        var std_dev = Math.Sqrt(2d / fan_in);

        for (int i = 0; i < weights.GetLength(0); i++)
        {
            for (int j = 0; j < weights.GetLength(1); j++)
            {
                weights[i, j] = Random.Shared.NextDouble() * std_dev * Math.Sqrt(12) - std_dev * Math.Sqrt(3);
            }
        }
    }

    private static void InitializeWeightsHeNormal(double[] biases)
    {
        var std_dev = Math.Sqrt(2d);

        for (int i = 0; i < biases.Length; i++)
        {
            biases[i] = Random.Shared.NextDouble() * std_dev * Math.Sqrt(12) - std_dev * Math.Sqrt(3);
        }
    }

    private static DenseVector NormalizedSoftmax(DenseVector input)
    {
        var max = input.Max();
        var exp = input.Select(x => Math.Exp(x - max)).ToArray();
        var sum = exp.Sum();
        return DenseVector.OfArray(exp.Select(x => x / sum).ToArray());
    }

    public class Model
    {
        public string[] Glyphs { get; set; } = [];

        public DenseMatrix[] Weights { get; set; } = [];

        public DenseVector[] Biases { get; set; } = [];

        public double Alpha { get; set; } = 0d;

        private Model(string[] glyphs, double alpha, DenseMatrix[] weights, DenseVector[] biases)
        {
            Glyphs = glyphs;
            Alpha = alpha;
            Weights = weights;
            Biases = biases;
        }

        public Model(ModelInitParams model_init_params)
        {
            Glyphs = model_init_params.Glyphs.ToArray();
            Alpha = model_init_params.Alpha;
            Weights = new DenseMatrix[1 + model_init_params.HiddenLayerCount];
            Biases = new DenseVector[1 + model_init_params.HiddenLayerCount];

            //Initialize weights
            for (int w = 0; w < Weights.Length; w++)
            {
                var next_activations_count = w == Weights.Length - 1
                    ? (uint)Glyphs.Length
                    : model_init_params.HiddenLayerNeuronCount;

                var neuron_count = w == 0
                    ? model_init_params.FeatureCount
                    : model_init_params.HiddenLayerNeuronCount;

                var layer = new double[next_activations_count, neuron_count];
                InitializeWeightsHeNormal(layer);
                Weights[w] = DenseMatrix.OfArray(layer);
            }    

            //Initialize biases
            for(int b = 0; b < Biases.Length; b++)
            {
                var next_activations_count = b == Biases.Length - 1
                    ? (uint)Glyphs.Length
                    : model_init_params.HiddenLayerNeuronCount;

                var layer = new double[next_activations_count];
                InitializeWeightsHeNormal(layer);
                Biases[b] = DenseVector.OfArray(layer);
            }
        }

        public Model Clone() => new([.. Glyphs], Alpha, Weights.Select(w => (DenseMatrix)w.Clone()).ToArray(), Biases.Select(b => (DenseVector)b.Clone()).ToArray());

        public byte[] ToBytes()
        {
            using var ms = new MemoryStream();

            //Write number of glyphs
            ms.Write(BitConverter.GetBytes(Glyphs.Length));

            //Write glyphs
            foreach(var glyph in Glyphs)
                ms.Write(Encoding.ASCII.GetBytes(glyph));

            //Write alpha
            ms.Write(BitConverter.GetBytes(Alpha));

            //Write number of layers
            ms.Write(BitConverter.GetBytes(Weights.Length));

            for(int w = 0; w < Weights.Length; w++)
            {
                var weights = Weights[w];
                var biases = Biases[w];

                //Write dimensions of layer
                ms.Write(BitConverter.GetBytes(weights.RowCount));
                ms.Write(BitConverter.GetBytes(weights.ColumnCount));

                //Write weights
                for (int i = 0; i < weights.RowCount; i++)
                {
                    for (int j = 0; j < weights.ColumnCount; j++)
                    {
                        ms.Write(BitConverter.GetBytes(weights[i, j]));
                    }
                }

                //Write biases
                for(int i = 0; i < biases.Count; i++)
                {
                    ms.Write(BitConverter.GetBytes(biases[i]));
                }
            }

            return ms.ToArray();
        }

        public static Model FromBytes(byte[] bytes)
        {
            int offset = 0;

            //Get number of glyphs
            int glyph_count = BitConverter.ToInt32(bytes, offset);
            offset += sizeof(int);

            //Get glyphs
            var glyphs = new string[glyph_count];

            for(int g = 0; g < glyph_count; g++)
            {
                glyphs[g] = Encoding.ASCII.GetString(bytes, offset, 1);
                offset += sizeof(byte);
            }

            //Get alpha
            double alpha = BitConverter.ToDouble(bytes, offset);
            offset += sizeof(double);

            //Get number of layers
            int layers = BitConverter.ToInt32(bytes, offset);
            offset += sizeof(int);

            var weights = new DenseMatrix[layers];
            var biases = new DenseVector[layers];

            for(int l = 0; l < layers; l++)
            {
                //Get dimensions of layer
                int m = BitConverter.ToInt32(bytes, offset);
                offset += sizeof(int);
                int n = BitConverter.ToInt32(bytes, offset);
                offset += sizeof(int);

                var layer = new double[m, n];

                //Read in weights for layer
                for(int i = 0; i < m; i++)
                {
                    for(int j = 0; j < n; j++)
                    {
                        layer[i, j] = BitConverter.ToDouble(bytes, offset);
                        offset += sizeof(double);
                    }
                }

                weights[l] = DenseMatrix.OfArray(layer);

                var bias = new double[m];

                //Read in biases for layer
                for(int i = 0; i < m; i++)
                {
                    bias[i] = BitConverter.ToDouble(bytes, offset);
                    offset += sizeof(double);
                }

                biases[l] = DenseVector.OfArray(bias);
            }

            return new(glyphs, alpha, weights, biases);
        }

    }

    public class ModelInitParams
    {
        public uint FeatureCount { get; set; } = 0;

        public uint HiddenLayerCount { get; set; } = 0;

        public uint HiddenLayerNeuronCount { get; set; } = 0;

        public double Alpha { get; set; } = 0;

        public IEnumerable<string> Glyphs { get; set; } = [];
    }

    public class TrainingSet
    {
        public double LearningRate { get; set; } = 0;

        public double LearningDecay { get; set; } = 0;

        public int Epoch { get; set; } = 0;

        public int Threads { get; set; } = 1;

        public IEnumerable<Input> Input { get; set; } = [];
    }

    public class Input
    {
        public double[] Intensities { get; set; } = [];

        public double[] SSIMs { get; set; } = [];

        public double[] NormalizedSSIMs
        {
            get
            {
                var sum = SSIMs.Sum();
                if (sum == 0)
                    return SSIMs;

                return SSIMs.Select(s => s / sum).ToArray();
            }
        }
    }

    public class Output
    {
        public string PredictedGlyph { get; set; } = string.Empty;
    }

    public class Options
    {
        public bool InvertFont { get; set; } = false;

        public bool NoColor { get; set; } = false;

        public int FontSize { get; set; } = 16;
    }
}
