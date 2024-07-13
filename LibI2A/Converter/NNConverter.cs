using ImageMagick;
using LibI2A.Common;
using MathNet.Numerics.LinearAlgebra.Double;
using MathNet.Numerics.Statistics;
using OneOf;
using System.Text;

namespace LibI2A.Converter;

/// <summary>
/// Allows the conversion of an Image to ASCII glyphs
/// using a trained neural net.
/// </summary>
public class NNConverter : IImageToASCIIConverter
{
    /// <summary>
    /// Epsilon for cross-entropy loss to prevent log(0)
    /// </summary>
    private const double EPS = 0.00001d;

    /// <summary>
    /// Epsilon for Adam calculation to prevent div by 0
    /// </summary>
    private const double ADAM_EPS = 0.0000001d;

    /// <summary>
    /// Allow a choice of any glyph within this of
    /// the best pick
    /// </summary>
    private const double PREDICTION_RANGE = 0.005d;

    /// <summary>
    /// For prediction, bucket hue to groups of this size
    /// </summary>
    private const int HUE_BUCKET = 25;

    /// <summary>
    /// Clamp gradients to this range
    /// </summary>
    private const double
        GRADIENT_MAX = 5d,
        GRADIENT_MIN = -5d;

    /// <summary>
    /// The trained neural net to use for predictions
    /// </summary>
    private readonly Model model;

    /// <summary>
    /// Options to modify image => glyph conversion
    /// </summary>
    private readonly Options options;

    /// <summary>
    /// Create a new converter from a trained model
    /// </summary>
    /// <param name="model">The trained neural net to use for predictions</param>
    /// <param name="configure">A delegate to configure the converter</param>
    public NNConverter(Model model, Action<Options>? configure = null)
    {
        this.model = model;
        Options options = new();
        configure?.Invoke(options);
        this.options = options;
    }

    /// <summary>
    /// Create a new converter from a trained model
    /// </summary>
    /// <param name="model">The trained neural net to use for predictions</param>
    /// <param name="options">Options to modify image => glyph conversion</param>
    public NNConverter(Model model, Options options)
    {
        this.model = model;
        this.options = options;
    }

    /// <summary>
    /// Convert the image to ASCII glyphs, using <see cref="model"/> to predict the
    /// corresponding glyph for each tile of the image.
    /// </summary>
    /// <param name="input">A stream containing the image to convert</param>
    /// <returns>An IEnumerable of ASCII glyphs and their colors.</returns>
    public IEnumerable<(string glyph, uint? color)> ConvertImage(Stream input)
    {
        using MagickImageCollection image_collection = new(input);
        image_collection.Coalesce();
        IMagickImage<ushort> image = image_collection.First();

        //Break images into windows
        PixelImage pixel_image = new(image);

        var width = (int)Math.Ceiling((double)image.Width / options.FontSize);
        var height = (int)Math.Ceiling((double)image.Height / options.FontSize);

        //Keep track of choices
        Dictionary<string, string> color_choices = [];
        var skips = new int[model.Glyphs.Length];

        var n = 0;

        foreach(PixelImage tile in pixel_image.Tiles(options.FontSize, options.FontSize))
        {
            if(n > 0 && (n % width) == 0)
            {
                yield return (Environment.NewLine, null);
            }

            //Get average color of tile
            var colors = tile.Pixels
                .Where(color => color != null)
                .Select(color => Utils.ARGBToAHSV((
                    Utils.ScaleUShort(color!.A),
                    Utils.ScaleUShort(color.R),
                    Utils.ScaleUShort(color.G),
                    Utils.ScaleUShort(color.B))))
                .ToList();

            uint combined = 0;
            uint hue_bucket = 0;

            if(colors.Count > 0)
            {
                (uint a, double h, double s, double v) sums = colors.AggregateOrDefault<(uint a, double h, double s, double v)>((a, b) => (a.a + b.a, a.h + b.h, a.s + b.s, a.v + b.v), (0, 0, 0, 0));
                (uint, double, double, double) avgs = ((uint)long.Clamp(sums.a / colors.Count, uint.MinValue, uint.MaxValue),
                    sums.h / colors.Count,
                    sums.s / colors.Count,
                    sums.v / colors.Count);
                (uint a, uint r, uint g, uint b) argb = Utils.AHSVToARGB(avgs);
                combined = Utils.ToUInt(Utils.AHSVToARGB(avgs));

                hue_bucket = (uint)Math.Round(avgs.Item2 * byte.MaxValue) / HUE_BUCKET * HUE_BUCKET;
            }

            //Predict the glyph that is most likely
            DenseVector[] activations = PropagateForwards(model, new Input()
            {
                Intensities = [.. tile.GetIntensities()],
            });

            DenseVector output = activations[^1];

            //Get highest probability
            var highest_probability = output.Max();

            //Get potential glyphs
            var choices = output.Select((n, ndx) => (n, ndx, glyph: model.Glyphs[ndx]))
                .Where(pair => highest_probability - pair.n < PREDICTION_RANGE)
                .OrderByDescending(pair => pair.glyph)
                .ToList();

            //Encode potential choices
            var encoded_choices = $"{hue_bucket};{string.Join(';', choices.Select(c => c.glyph))}";

            //If a glyph was already chosen for this color / choice combo, use that
            if(!color_choices.TryGetValue(encoded_choices, out var glyph))
            {
                //Choose glyph based on probability and number of times it wasn't chosen
                glyph = choices.OrderByDescending(c => skips[c.ndx])
                    .ThenByDescending(c => c.n)
                    .ThenBy(c => Random.Shared.Next())
                    .FirstOrDefault().glyph;

                //Choose a random glyph from the given class
                if(options.GlyphClasses.TryGetValue(glyph, out var glyph_class) && glyph_class.Length > 0)
                {
                    glyph = glyph_class[Random.Shared.Next(glyph_class.Length)];
                }

                //Record selected choice
                color_choices[encoded_choices] = glyph;

                //Increment skips for non-selected glyphs
                foreach((double n, int ndx, string glyph) choice in choices)
                {
                    if(choice.glyph == glyph)
                    {
                        skips[choice.ndx] = 0;
                    }
                    else
                    {
                        skips[choice.ndx]++;
                    }
                }
            }

            yield return (glyph, combined);
            n++;
        }

        yield break;
    }

    /// <summary>
    /// Create a new neural net based on the given params, and train it.
    /// </summary>
    /// <param name="model_init_params">The params for the new neural net</param>
    /// <param name="training_set">The data to train the model on</param>
    /// <param name="log">A delegate to log messages</param>
    /// <param name="on_batch_complete">Optional action to perform when a batch is completed</param>
    /// <param name="token">A cancellation token to end the task early.</param>
    /// <returns>Either the trained <see cref="Model"/>, or a <see cref="TrainingError"/>.</returns>
    public static Task<OneOf<Model, TrainingError>> TrainAsync(ModelInitParams model_init_params, TrainingSet training_set,
        Action<string, bool> log, Func<Model, CancellationToken, Task>? on_batch_complete = null, CancellationToken token = default) => TrainAsync(new Model(model_init_params), training_set, log, on_batch_complete, token);

    /// <summary>
    /// Continue the training of an existing model.
    /// </summary>
    /// <param name="model_initial">The model to train.</param>
    /// <param name="training_set">The data to train the model on</param>
    /// <param name="Log">A delegate to log messages</param>
    /// <param name="on_batch_complete">Optional action to perform when a batch is completed</param>
    /// <param name="token">A cancellation token to end the task early.</param>
    /// <returns>Either the trained <see cref="Model"/>, or a <see cref="TrainingError"/>.</returns>
    public static async Task<OneOf<Model, TrainingError>> TrainAsync(Model model_initial, TrainingSet training_set,
        Action<string, bool> log, Func<Model, CancellationToken, Task>? on_batch_complete = null, CancellationToken token = default)
    {
        void Log(string message, bool is_error = false) => log(message, is_error);

        TrainingError Error(string message, Exception? ex = null)
        {
            Log(message, true);
            return new TrainingError(message, ex);
        }

        if(model_initial.Glyphs.Length == 0)
        {
            return Error("No glyphs were provided.");
        }

        try
        {
            token.ThrowIfCancellationRequested();

            IEnumerator<Input[]> batches = training_set.Input.Chunk(training_set.BatchSize).GetEnumerator();
            Model model = model_initial;

            //Init Adam
            Adam adam = new(training_set.LearningRate, training_set.AdamParams.beta_1, training_set.AdamParams.beta_2, model);

            for(var epoch = 0; batches.MoveNext(); epoch++)
            {
                token.ThrowIfCancellationRequested();

                Input[] batch = batches.Current;

                //Calculate class weights as being inversely proportional to frequency and confidence
                DenseVector batch_ssim_freq = batch.Select(b => DenseVector.OfArray(b.NormalizedSSIMs))
                    .Aggregate((a, b) => a + b);

                //Normalize based on z-score
                var batch_ssim_mean = batch_ssim_freq.Mean();
                var batch_ssim_stddev = batch_ssim_freq.StandardDeviation();

                batch_ssim_freq.MapInplace(s => (s - batch_ssim_mean) / (batch_ssim_stddev == 0 ? 1 : batch_ssim_stddev));

                //Scale to the range 0-1
                var batch_ssim_min = batch_ssim_freq.Min();
                var batch_ssim_max = batch_ssim_freq.Max() - batch_ssim_min;

                batch_ssim_freq.MapInplace(s => (s - batch_ssim_min) / (batch_ssim_max == 0 ? 1 : batch_ssim_max));

                //Get logistic weights
                const double K = 8d, C = 0.4d;
                var class_weights = (DenseVector)batch_ssim_freq.Map(s => 1 - (1d / (1 + Math.Exp(-K * (s - C)))) + (1d / (1 + Math.Exp(K * C))));

                SemaphoreSlim tasks = new(training_set.Threads, training_set.Threads);

                List<Task<(DenseVector[] activations, DenseVector pointwise_loss, double loss)>> forward_propagation_results = [];

                //Run batch through the neural net
                foreach(Input? input in batch)
                {
                    token.ThrowIfCancellationRequested();

                    //Wait until there's an available task
                    await tasks.WaitAsync(token);

                    //Run the example forward through the neural net
                    forward_propagation_results.Add(Task.Run(() =>
                    {
                        try
                        {
                            token.ThrowIfCancellationRequested();

                            DenseVector[] activations = PropagateForwards(model, input);

                            //Compare the output to the expected value
                            DenseVector output = activations[^1];

                            var ssims = DenseVector.OfArray(input.NormalizedSSIMs);

                            //Calculate weighted error using cross entropy loss
                            var loss = ssims * output.Map(value => Math.Log(Math.Max(EPS, value)))
                                .PointwiseMultiply(class_weights);

                            //Calculate weighted pointwise loss using binary cross entropy loss
                            MathNet.Numerics.LinearAlgebra.Vector<double> pointwise_loss = -1 * (ssims.PointwiseMultiply(output.Map(value => Math.Log(Math.Max(EPS, value))))
                                + ssims.Map(s => 1 - s).PointwiseMultiply(output.Map(value => Math.Log(Math.Max(EPS, 1 - value)))))
                                .PointwiseMultiply(class_weights);

                            token.ThrowIfCancellationRequested();

                            return (activations, (DenseVector)pointwise_loss, -loss);
                        }
                        finally
                        {
                            //Release handle so another task can use it
                            _ = tasks.Release();
                        }
                    }, token));
                }

                token.ThrowIfCancellationRequested();

                //Wait for all forward propagation tasks to complete
                (DenseVector[] activations, DenseVector pointwise_loss, double loss)[] batch_activations = await Task.WhenAll(forward_propagation_results);

                token.ThrowIfCancellationRequested();

                //Get average loss across batch
                var avg_loss = batch_activations.Select(a => a.loss).Average();

                Log($"Batch loss: {avg_loss}");

                //Get average loss for each batch
                DenseVector avg_class_loss = batch_activations.Select(a => a.pointwise_loss)
                    .Aggregate((a, b) => a + b) / batch_activations.Length;

                List<Task<(DenseMatrix[]? weights, DenseVector[]? biases)>> backward_propagation_results = [];

                //Propagate batch backwards through the neural net
                for(var i = 0; i < batch.Length; i++)
                {
                    Input input = batch[i];
                    DenseVector[] activations = batch_activations[i].activations;

                    token.ThrowIfCancellationRequested();

                    //Wait until there's an available task
                    await tasks.WaitAsync(token);

                    //Propagate example backwards through the neural net
                    backward_propagation_results.Add(Task.Run(() =>
                    {
                        try
                        {
                            token.ThrowIfCancellationRequested();

                            return PropagateBackwards(model, input, activations, class_weights);
                        }
                        finally
                        {
                            //Release handle so another task can use it
                            _ = tasks.Release();
                        }
                    }, token));
                }

                token.ThrowIfCancellationRequested();

                //Wait for all backward propagation tasks to complete
                (DenseMatrix[]? weights, DenseVector[]? biases)[] batch_gradients = await Task.WhenAll(backward_propagation_results);

                token.ThrowIfCancellationRequested();

                //Average gradients across all trials
                var nn_gradients = batch_gradients.Where(g => g.weights != null && g.biases != null).Select(g => (weights: g.weights!, biases: g.biases!)).ToList();
                (DenseMatrix[] weights, DenseVector[] biases) first_trial_gradients = nn_gradients.First();
                var average_gradients = new DenseMatrix[model.Weights.Length];
                var average_bias_gradients = new DenseVector[model.Weights.Length];

                for(var l = 0; l < model.Weights.Length; l++)
                {
                    token.ThrowIfCancellationRequested();

                    //Sum gradients from each trial
                    DenseMatrix sum = new(first_trial_gradients.weights[l].RowCount, first_trial_gradients.weights[l].ColumnCount);
                    DenseVector bias_sum = new(first_trial_gradients.biases[l].Count);

                    foreach((DenseMatrix[] weights, DenseVector[] biases) gradient in nn_gradients)
                    {
                        token.ThrowIfCancellationRequested();

                        sum += gradient.weights[l];
                        bias_sum += gradient.biases[l];
                    }

                    //Divide sum by count to get average
                    sum = (DenseMatrix)(sum / nn_gradients.Count);
                    bias_sum /= nn_gradients.Count;
                    average_gradients[l] = sum;
                    average_bias_gradients[l] = bias_sum;
                }

                token.ThrowIfCancellationRequested();

                model = model.Clone();

                //Update weights and biases using Adam
                UpdateAdamWeights(adam, model, average_gradients, average_bias_gradients, training_set.Lambda, epoch);

                token.ThrowIfCancellationRequested();

                //Invoke callback on batch complete
                await (on_batch_complete?.Invoke(model, token) ?? Task.CompletedTask);
            }

            //Return the trained model
            return model;
        }
        catch(Exception e)
        {
            return Error(e.Message, e);
        }
    }

    /// <summary>
    /// Propagate the input forward through the neural net.
    /// </summary>
    /// <param name="model">The model to propagate through.</param>
    /// <param name="input">The input.</param>
    /// <returns>The activations from each layer of the neural net.</returns>
    private static DenseVector[] PropagateForwards(Model model, Input input)
    {
        var neurons = DenseVector.OfArray(input.NormalizedIntensities);

        var activations = new DenseVector[model.Weights.Length + 1];
        activations[0] = neurons;

        //For each layer, propagating forward
        for(var l = 0; l < model.Weights.Length; l++)
        {
            DenseMatrix layer = model.Weights[l];
            DenseVector bias = model.Biases[l];

            //Take dot product of weights and neurons, and add bias to get activation inputs
            DenseVector product = (layer * neurons) + bias;

            if(l == model.Weights.Length - 1)
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
            activations[l + 1] = neurons;
        }

        return activations;
    }

    /// <summary>
    /// Propagate backwards through the neural net, recomputing gradients.
    /// </summary>
    /// <param name="model">The model to propagate through.</param>
    /// <param name="input">The input.</param>
    /// <param name="activations">The activations from each layer of the neural net.</param>
    /// <returns>The gradients for the weights and biases.</returns>
    private static (DenseMatrix[]? weight_gradients, DenseVector[]? bias_gradients) PropagateBackwards(Model model, Input input, DenseVector[] activations, DenseVector class_weights)
    {
        var ssims = DenseVector.OfArray(input.NormalizedSSIMs);

        var all_gradients = new DenseMatrix[model.Weights.Length];
        var all_errors = new DenseVector[model.Weights.Length];
        var all_biases = new DenseVector[model.Weights.Length];

        for(var l = all_gradients.Length - 1; l >= 0; l--)
        {
            //Calculate the partial derivative of loss wrt to the weights of layer l
            DenseMatrix weights = model.Weights[l];
            DenseVector neurons = activations[l + 1];

            var layer_gradients = new DenseVector[weights.RowCount];
            DenseVector layer_bias = new(weights.RowCount);

            DenseVector error_term;

            if(l == all_gradients.Length - 1)
            {
                //Gradient of cross-entropy loss wrt softmax activation simplifies to predicted - expected
                //Multiply by class weights
                error_term = (DenseVector)(neurons - ssims).PointwiseMultiply(class_weights);
            }
            else
            {
                //Get weights and error terms for next layer
                DenseMatrix next_weights = model.Weights[l + 1];
                DenseVector next_errors = all_errors[l + 1];

                //Using chain rule, error term is the transpose of the next layer's weights times it's error term,
                //times the derivative of ReLU
                error_term = (DenseVector)next_weights.TransposeThisAndMultiply(next_errors)
                    .PointwiseMultiply(neurons.Map(n => n <= 0 ? model.Alpha : 1));
            }

            all_errors[l] = error_term;

            //Grad of loss wrt bias is just error
            all_biases[l] = DenseVector.OfArray(error_term.Select(n => double.Clamp(n, GRADIENT_MIN, GRADIENT_MAX)).ToArray());

            //Grad of loss wrt weights is error * prev activations transpose (chain rule)
            all_gradients[l] = (DenseMatrix)(error_term.ToColumnMatrix() * activations[l].ToRowMatrix()).Map(n => double.Clamp(n, GRADIENT_MIN, GRADIENT_MAX));
        }

        return (all_gradients, all_biases);
    }

    /// <summary>
    /// Update adam moments and model weights
    /// </summary>
    /// <param name="adam"></param>
    /// <param name="model"></param>
    /// <param name="gradients"></param>
    /// <param name="bias_gradients"></param>
    /// <param name="l2_coeff"></param>
    /// <param name="epoch"></param>
    private static void UpdateAdamWeights(Adam adam, Model model, DenseMatrix[] gradients, DenseVector[] bias_gradients, double l2_coeff, int epoch)
    {
        for(var l = 0; l < model.Weights.Length; l++)
        {
            //Update first moment
            adam.Moment1[l] = (adam.Decay.Beta1 * adam.Moment1[l]) + ((1 - adam.Decay.Beta1) * gradients[l]);
            adam.Bias1[l] = (adam.Decay.Beta1 * adam.Bias1[l]) + ((1 - adam.Decay.Beta1) * bias_gradients[l]);

            //Update second moment
            adam.Moment2[l] = (DenseMatrix)((adam.Decay.Beta2 * adam.Moment2[l]) + ((1 - adam.Decay.Beta2) * gradients[l].PointwiseMultiply(gradients[l])));
            adam.Bias2[l] = (DenseVector)((adam.Decay.Beta2 * adam.Bias2[l]) + ((1 - adam.Decay.Beta2) * bias_gradients[l].PointwiseMultiply(bias_gradients[l])));

            //Compute bias-corrected first moment
            MathNet.Numerics.LinearAlgebra.Matrix<double> moment1_corrected = adam.Moment1[l] / (1 - Math.Pow(adam.Decay.Beta1, epoch + 1));
            DenseVector bias1_corrected = adam.Bias1[l] / (1 - Math.Pow(adam.Decay.Beta1, epoch + 1));

            //Compute bias-corrected second moment
            MathNet.Numerics.LinearAlgebra.Matrix<double> moment2_corrected = adam.Moment2[l] / (1 - Math.Pow(adam.Decay.Beta2, epoch + 1));
            DenseVector bias2_corrected = adam.Bias2[l] / (1 - Math.Pow(adam.Decay.Beta2, epoch + 1));

            MathNet.Numerics.LinearAlgebra.Matrix<double> adam_learning_rate = moment1_corrected.PointwiseDivide(moment2_corrected.Map(m => Math.Sqrt(m) + ADAM_EPS));
            MathNet.Numerics.LinearAlgebra.Vector<double> adam_bias_learning_rate = bias1_corrected.PointwiseDivide(bias2_corrected.Map(m => Math.Sqrt(m) + ADAM_EPS));

            //L2 Regularization
            adam_learning_rate += l2_coeff * model.Weights[l];

            //Update weights and biases
            model.Weights[l] -= (DenseMatrix)(adam.LearningRate * adam_learning_rate);
            model.Biases[l] -= (DenseVector)(adam.LearningRate * adam_bias_learning_rate);
        }
    }

    /// <summary>
    /// Initialize the weights matrix use He Initialization
    /// </summary>
    /// <param name="weights">The matrix to initialize</param>
    private static void InitializeWeightsHeNormal(double[,] weights)
    {
        var fan_in = weights.GetLength(1);
        var std_dev = Math.Sqrt(2d / fan_in);

        for(var i = 0; i < weights.GetLength(0); i++)
        {
            for(var j = 0; j < weights.GetLength(1); j++)
            {
                weights[i, j] = (Random.Shared.NextDouble() * std_dev * Math.Sqrt(12)) - (std_dev * Math.Sqrt(3));
            }
        }
    }

    /// <summary>
    /// Initialize the bias vector use He Initialization
    /// </summary>
    /// <param name="biases">The vector to initialize</param>
    private static void InitializeWeightsHeNormal(double[] biases)
    {
        var std_dev = Math.Sqrt(2d);

        for(var i = 0; i < biases.Length; i++)
        {
            biases[i] = (Random.Shared.NextDouble() * std_dev * Math.Sqrt(12)) - (std_dev * Math.Sqrt(3));
        }
    }

    /// <summary>
    /// Softmax with log-sum-exp
    /// </summary>
    /// <param name="input">The vector to apply the softmax to</param>
    /// <returns>The input with softmax applied</returns>
    private static DenseVector NormalizedSoftmax(DenseVector input)
    {
        var max = input.Max();
        var exp = input.Select(x => Math.Exp(x - max)).ToArray();
        var sum = exp.Sum();
        return DenseVector.OfArray(exp.Select(x => x / sum).ToArray());
    }

    /// <summary>
    /// A trained neural network to classify an image tile as an ASCII glyph.
    /// </summary>
    public class Model : IEquatable<Model?>
    {
        /// <summary>
        /// The glyphs available for classification, one-hot encoded
        /// by their index in the array.
        /// </summary>
        public string[] Glyphs { get; set; } = [];

        /// <summary>
        /// The weights at each layer of the neural net
        /// </summary>
        public DenseMatrix[] Weights { get; set; } = [];

        /// <summary>
        /// The biases at each layer of the neural net
        /// </summary>
        public DenseVector[] Biases { get; set; } = [];

        /// <summary>
        /// The alpha value to use for Leaky ReLU
        /// </summary>
        public double Alpha { get; set; } = 0d;

        private Model(string[] glyphs, double alpha, DenseMatrix[] weights, DenseVector[] biases)
        {
            Glyphs = glyphs;
            Alpha = alpha;
            Weights = weights;
            Biases = biases;
        }

        /// <summary>
        /// Initialize the model with the given params
        /// </summary>
        /// <param name="model_init_params"></param>
        public Model(ModelInitParams model_init_params)
        {
            Glyphs = model_init_params.Glyphs.ToArray();
            Alpha = model_init_params.Alpha;
            Weights = new DenseMatrix[1 + model_init_params.HiddenLayerCount];
            Biases = new DenseVector[1 + model_init_params.HiddenLayerCount];

            //Initialize weights
            for(var w = 0; w < Weights.Length; w++)
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
            for(var b = 0; b < Biases.Length; b++)
            {
                var next_activations_count = b == Biases.Length - 1
                    ? (uint)Glyphs.Length
                    : model_init_params.HiddenLayerNeuronCount;

                var layer = new double[next_activations_count];
                InitializeWeightsHeNormal(layer);
                Biases[b] = DenseVector.OfArray(layer);
            }
        }

        /// <summary>
        /// Create a deep copy of the model
        /// </summary>
        /// <returns></returns>
        public Model Clone() => new([.. Glyphs], Alpha, Weights.Select(w => (DenseMatrix)w.Clone()).ToArray(), Biases.Select(b => (DenseVector)b.Clone()).ToArray());

        /// <summary>
        /// Serialize the model as an array of bytes.
        /// </summary>
        /// <returns>An array of bytes representing the model</returns>
        public byte[] ToBytes()
        {
            using MemoryStream ms = new();

            //Write number of glyphs
            ms.Write(BitConverter.GetBytes(Glyphs.Length));

            //Write glyphs
            foreach(var glyph in Glyphs)
            {
                ms.Write(Encoding.ASCII.GetBytes(glyph));
            }

            //Write alpha
            ms.Write(BitConverter.GetBytes(Alpha));

            //Write number of layers
            ms.Write(BitConverter.GetBytes(Weights.Length));

            for(var w = 0; w < Weights.Length; w++)
            {
                DenseMatrix weights = Weights[w];
                DenseVector biases = Biases[w];

                //Write dimensions of layer
                ms.Write(BitConverter.GetBytes(weights.RowCount));
                ms.Write(BitConverter.GetBytes(weights.ColumnCount));

                //Write weights
                for(var i = 0; i < weights.RowCount; i++)
                {
                    for(var j = 0; j < weights.ColumnCount; j++)
                    {
                        ms.Write(BitConverter.GetBytes(weights[i, j]));
                    }
                }

                //Write biases
                for(var i = 0; i < biases.Count; i++)
                {
                    ms.Write(BitConverter.GetBytes(biases[i]));
                }
            }

            return ms.ToArray();
        }

        /// <summary>
        /// Deserialize the model from an array of bytes.
        /// </summary>
        /// <param name="bytes">The serialized model.</param>
        /// <returns>A model.</returns>
        public static Model FromBytes(byte[] bytes)
        {
            var offset = 0;

            //Get number of glyphs
            var glyph_count = BitConverter.ToInt32(bytes, offset);
            offset += sizeof(int);

            //Get glyphs
            var glyphs = new string[glyph_count];

            for(var g = 0; g < glyph_count; g++)
            {
                glyphs[g] = Encoding.ASCII.GetString(bytes, offset, 1);
                offset += sizeof(byte);
            }

            //Get alpha
            var alpha = BitConverter.ToDouble(bytes, offset);
            offset += sizeof(double);

            //Get number of layers
            var layers = BitConverter.ToInt32(bytes, offset);
            offset += sizeof(int);

            var weights = new DenseMatrix[layers];
            var biases = new DenseVector[layers];

            for(var l = 0; l < layers; l++)
            {
                //Get dimensions of layer
                var m = BitConverter.ToInt32(bytes, offset);
                offset += sizeof(int);
                var n = BitConverter.ToInt32(bytes, offset);
                offset += sizeof(int);

                var layer = new double[m, n];

                //Read in weights for layer
                for(var i = 0; i < m; i++)
                {
                    for(var j = 0; j < n; j++)
                    {
                        layer[i, j] = BitConverter.ToDouble(bytes, offset);
                        offset += sizeof(double);
                    }
                }

                weights[l] = DenseMatrix.OfArray(layer);

                var bias = new double[m];

                //Read in biases for layer
                for(var i = 0; i < m; i++)
                {
                    bias[i] = BitConverter.ToDouble(bytes, offset);
                    offset += sizeof(double);
                }

                biases[l] = DenseVector.OfArray(bias);
            }

            return new(glyphs, alpha, weights, biases);
        }

        public override bool Equals(object? obj) => Equals(obj as Model);

        public bool Equals(Model? other)
        {
            return other is not null &&
                Glyphs.SequenceEqual(other.Glyphs) &&
                Weights.SequenceEqual(other.Weights) &&
                Biases.SequenceEqual(other.Biases) &&
                Alpha == other.Alpha;
        }

        public override int GetHashCode() => HashCode.Combine(Glyphs, Weights, Biases, Alpha);

        public static bool operator ==(Model? left, Model? right) => ((left is null) == (right is null)) && (left is null || left.Equals(right));

        public static bool operator !=(Model? left, Model? right) => !(left == right);
    }

    /// <summary>
    /// Parameters for creating a new model
    /// </summary>
    public class ModelInitParams
    {
        /// <summary>
        /// The number of features the neural net accepts
        /// </summary>
        public uint FeatureCount { get; set; } = 0;

        /// <summary>
        /// The number of hidden layers the neural net contains
        /// </summary>
        public uint HiddenLayerCount { get; set; } = 0;

        /// <summary>
        /// The number of neurons each hidden layer contains
        /// </summary>
        public uint HiddenLayerNeuronCount { get; set; } = 0;

        /// <summary>
        /// The alpha value to use for Leaky ReLU
        /// </summary>
        public double Alpha { get; set; } = 0;

        /// <summary>
        /// The glyphs to use as classes
        /// </summary>
        public IEnumerable<string> Glyphs { get; set; } = [];
    }

    /// <summary>
    /// The input data for training a model
    /// </summary>
    public class TrainingSet
    {
        /// <summary>
        /// Coefficient to multiply the gradient by when updating weights
        /// </summary>
        public double LearningRate { get; set; } = 0;

        /// <summary>
        /// The number of parallel computations allowed at a time.
        /// </summary>
        public int Threads { get; set; } = 1;

        /// <summary>
        /// The number of training examples to be batched together.
        /// </summary>
        public int BatchSize { get; set; } = 1;

        /// <summary>
        /// L2 Regularization coefficient
        /// </summary>
        public double Lambda { get; set; } = 0;

        /// <summary>
        /// The beta 1 and beta 2 params for Adam
        /// </summary>
        public (double beta_1, double beta_2) AdamParams { get; set; } = (0, 0);

        /// <summary>
        /// The training examples.
        /// </summary>
        public IEnumerable<Input> Input { get; set; } = [];
    }

    /// <summary>
    /// Data for Adam optimization
    /// </summary>
    public class Adam
    {
        /// <summary>
        /// Learning rate for the neural net
        /// </summary>
        public double LearningRate { get; set; } = 0d;

        /// <summary>
        /// Exponential decay rates for each step
        /// </summary>
        public (double Beta1, double Beta2) Decay { get; set; } = (0d, 0d);

        /// <summary>
        /// Gradients
        /// </summary>
        public DenseMatrix[] Moment1 { get; set; } = [];

        /// <summary>
        /// Square of the gradients
        /// </summary>
        public DenseMatrix[] Moment2 { get; set; } = [];

        /// <summary>
        /// Bias for first moment
        /// </summary>
        public DenseVector[] Bias1 { get; set; } = [];

        /// <summary>
        /// Bias for second moment
        /// </summary>
        public DenseVector[] Bias2 { get; set; } = [];

        public Adam(double learning_rate, double decay_1, double decay_2, Model model)
        {
            LearningRate = learning_rate;
            Decay = (decay_1, decay_2);

            Moment1 = new DenseMatrix[model.Weights.Length];
            Moment2 = new DenseMatrix[model.Weights.Length];
            Bias1 = new DenseVector[model.Weights.Length];
            Bias2 = new DenseVector[model.Weights.Length];

            //Init all values to 0
            for(var i = 0; i < model.Weights.Length; i++)
            {
                Moment1[i] = new DenseMatrix(model.Weights[i].RowCount, model.Weights[i].ColumnCount);
                Moment2[i] = new DenseMatrix(model.Weights[i].RowCount, model.Weights[i].ColumnCount);
                Bias1[i] = new DenseVector(model.Weights[i].RowCount);
                Bias2[i] = new DenseVector(model.Weights[i].RowCount);
            }
        }
    }

    /// <summary>
    /// A training example for the neural net
    /// </summary>
    public class Input
    {
        private const double
            COERCE_TO_ZERO = 0.00001,
            STD_DEV = 0.15d;

        /// <summary>
        /// The intensities of each pixel in this image tile.
        /// </summary>
        public double[] Intensities { get; set; } = [];

        /// <Intensities>
        /// The Intensities, standardized to have mean of 0 and std deviation of 1
        /// </summary>
        public double[] NormalizedIntensities
        {
            get
            {
                var mean = Intensities.Mean();
                var std_dev = Intensities.StandardDeviation();

                return std_dev == 0 || double.IsNaN(mean) || double.IsNaN(std_dev) ? Intensities : ([.. Intensities.Select(s => (s - mean) / std_dev)]);
            }
        }

        /// <summary>
        /// The Structural Similarity Index between this image tile
        /// and each glyph.
        /// </summary>
        public double[] SSIMs { get; set; } = [];

        /// <summary>
        /// The SSIMs, gaussian weighted by their distance from the max
        ///</summary>
        public double[] NormalizedSSIMs
        {
            get
            {
                var max = SSIMs.Max();

                if(max == 0)
                {
                    return SSIMs;
                }

                //Get gaussian weights based on percentage of max
                var weights = DenseVector.OfEnumerable(SSIMs.Select(s => Math.Exp(-Math.Pow((s / max) - 1, 2) / (2 * STD_DEV * STD_DEV)) / (Math.Sqrt(2 * Math.PI) * STD_DEV)));

                //Penalize based on percentage of max
                MathNet.Numerics.LinearAlgebra.Vector<double> penalized = weights.PointwiseMultiply(DenseVector.OfArray(SSIMs))
                    .Map(s => s <= COERCE_TO_ZERO ? 0 : s);

                //Divide by sum to add up to 1
                var sum = penalized.Sum();

                return sum == 0 ? ([.. penalized]) : penalized.Select(p => p / sum).ToArray();
            }
        }
    }

    /// <summary>
    /// Options to apply to image => ASCII conversion
    /// </summary>
    public class Options
    {
        /// <summary>
        /// If true, will use white-on-black text rather than black-on-white
        /// </summary>
        public bool InvertFont { get; set; } = false;

        /// <summary>
        /// The tile/font size to use
        /// </summary>
        public int FontSize { get; set; } = 16;

        /// <summary>
        /// Glyphs that are equivalent for the purposes of SSIM
        /// </summary>
        public Dictionary<string, string[]> GlyphClasses = [];
    }

    /// <summary>
    /// An error that occurred during training of the neural net.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="ex">The optional inner exception.</param>
    public class TrainingError(string message, Exception? ex = null)
    {
        /// <summary>
        /// The error message
        /// </summary>
        public string Message { get; init; } = message;

        /// <summary>
        /// The inner exception
        /// </summary>
        public Exception? Exception { get; init; } = ex;
    }
}
