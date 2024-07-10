using MathNet.Numerics.LinearAlgebra.Double;
using MathNet.Numerics.Statistics;
using OneOf;
using System.Text;
using static LibI2A.Calculator.SSIMPredictionModel;

namespace LibI2A.Calculator;
public class SSIMPredictionCalculator : ISSIMCalculator
{
    private readonly SSIMPredictionModel model;

    private readonly Settings settings;

    public SSIMPredictionCalculator(SSIMPredictionModel model, Action<Settings>? configure = null)
    {
        this.model = model;
        var settings = new Settings();
        configure?.Invoke(settings);
        this.settings = settings;
    }

    public SSIMPredictionCalculator(SSIMPredictionModel model, Settings settings)
    {
        this.model = model;
        this.settings = settings;
    }

    public double CalculateSSIM(PixelImage image, string glyph)
    {
        if (!model.GlyphModels.TryGetValue(glyph, out var submodel))
            return 0d;

        var activations = SSIMPredictionTrainer.PropagateForwards(submodel, new()
        {
            Intensities = [.. image.GetIntensities()],
            SSIMs = []
        });

        //Get output
        return activations[^1][0];
    }

    public class Settings
    {

    }
}

public static class SSIMPredictionTrainer
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
    /// Clamp gradients to this range
    /// </summary>
    private const double
        GRADIENT_MAX = 2d,
        GRADIENT_MIN = -2d;

    public static Task<OneOf<SSIMPredictionModel, TrainingError>> TrainAsync(SSIMPredictionModelInitParams model_init_params, IEnumerable<SSIMPredictionInput> training_set,
        TrainingSettings settings, Action<string, bool> log, Func<SSIMPredictionModel, CancellationToken, Task>? on_batch_complete = null, CancellationToken token = default)
        => TrainAsync(new SSIMPredictionModel(model_init_params), training_set, settings, log, on_batch_complete, token);

    public static async Task<OneOf<SSIMPredictionModel, TrainingError>> TrainAsync(SSIMPredictionModel model_initial, IEnumerable<SSIMPredictionInput> training_set,
        TrainingSettings settings, Action<string, bool> log, Func<SSIMPredictionModel, CancellationToken, Task>? on_batch_complete = null, CancellationToken token = default)
    {
        void Log(string message, bool is_error = false) => log(message, is_error);

        TrainingError Error(string message, Exception? ex = null)
        {
            Log(message, true);
            return new TrainingError(message, ex);
        }

        if (model_initial.GlyphModels.Count == 0)
            return Error("No glyphs were provided.");

        try
        {
            token.ThrowIfCancellationRequested();

            IEnumerator<SSIMPredictionInput[]> batches = training_set.Chunk(settings.BatchSize).GetEnumerator();
            SSIMPredictionModel model = model_initial;

            //Init adam
            var adams = new Adam[model.Glyphs.Length];
            for(int g = 0; g < model.Glyphs.Length; g++)
            {
                var submodel = model.GlyphModels[model.Glyphs[g]];
                adams[g] = new(settings.LearningRate, settings.AdamParams.beta_1, settings.AdamParams.beta_2, submodel);
            }

            //Run each mini-batch
            for (int epoch = 0; batches.MoveNext(); epoch++)
            {
                model = model.Clone();

                token.ThrowIfCancellationRequested();
                
                var batch = batches.Current;

                SemaphoreSlim tasks = new(settings.Threads, settings.Threads);

                //Run each glyph-model (one-v-all)
                for(int g = 0; g < model.Glyphs.Length; g++)
                {
                    var adam = adams[g];
                    var submodel = model.GlyphModels[model.Glyphs[g]];

                    List<Task<(DenseVector[] activations, double loss)>> propagate_forwards = [];

                    //Propagate each input forwards through the model
                    foreach (var input in batch)
                    {
                        token.ThrowIfCancellationRequested();
                        await tasks.WaitAsync(token);

                        //Propagate input forward
                        propagate_forwards.Add(Task.Run(() =>
                        {
                            try
                            {
                                token.ThrowIfCancellationRequested();

                                var activations = PropagateForwards(submodel, input);

                                token.ThrowIfCancellationRequested();

                                //Compare the output to the expected value
                                var output = activations[^1][0];
                                var expected = input.SSIMs[g];

                                //Calculate loss using binary cross entropy loss]
                                double loss = expected * Math.Log(output + EPS) + (1 - expected) * Math.Log(1 - output + EPS);

                                return (activations, loss);
                            }
                            finally
                            {
                                tasks.Release();
                            }
                        }, token));
                    }

                    token.ThrowIfCancellationRequested();

                    //Wait for all forward propagation to complete
                    var propagate_forwards_results = await Task.WhenAll(propagate_forwards);

                    token.ThrowIfCancellationRequested();

                    //Get average loss across batch
                    double avg_loss = propagate_forwards_results.Select(r => r.loss).Average();
                    Log($"Epoch: {epoch}; Glyph: {model.Glyphs[g]}; Avg Loss: {avg_loss}");

                    List<Task<(DenseMatrix[] weight_gradients, DenseVector[] bias_gradients)>> propagate_backwards = [];

                    //Propagate each input backwards through the model
                    for(int i = 0; i < batch.Length; i++)
                    {
                        token.ThrowIfCancellationRequested();

                        var input = batch[i];
                        (var activations, _) = propagate_forwards_results[i];

                        await tasks.WaitAsync(token);

                        //Propagate input backwards
                        propagate_backwards.Add(Task.Run(() =>
                        {
                            try
                            {
                                token.ThrowIfCancellationRequested();
                                return PropagateBackwards(submodel, input.SSIMs[g], activations);
                            }
                            finally
                            {
                                tasks.Release();
                            }
                        }, token));
                    }

                    token.ThrowIfCancellationRequested();

                    //Wait for all backward propagation to complete
                    var propagate_backwards_results = await Task.WhenAll(propagate_backwards);

                    token.ThrowIfCancellationRequested();

                    //Average gradients across all trials
                    (DenseMatrix[] weights, DenseVector[] biases) first_trial_gradients = propagate_backwards_results.First();
                    DenseMatrix[] average_gradients = new DenseMatrix[submodel.Weights.Length];
                    DenseVector[] average_bias_gradients = new DenseVector[submodel.Weights.Length];

                    for (int l = 0; l < submodel.Weights.Length; l++)
                    {
                        token.ThrowIfCancellationRequested();

                        //Sum gradients from each trial
                        DenseMatrix sum = new(first_trial_gradients.weights[l].RowCount, first_trial_gradients.weights[l].ColumnCount);
                        DenseVector bias_sum = new(first_trial_gradients.biases[l].Count);

                        foreach ((DenseMatrix[] weight_gradients, DenseVector[] bias_gradients) gradient in propagate_backwards_results)
                        {
                            token.ThrowIfCancellationRequested();

                            sum += gradient.weight_gradients[l];
                            bias_sum += gradient.bias_gradients[l];
                        }

                        //Divide sum by count to get average
                        sum = (DenseMatrix)(sum / propagate_backwards_results.Length);
                        bias_sum /= propagate_backwards_results.Length;
                        average_gradients[l] = sum;
                        average_bias_gradients[l] = bias_sum;
                    }

                    token.ThrowIfCancellationRequested();

                    //Update weights and biases using Adam
                    UpdateAdamWeights(adam, submodel, average_gradients, average_bias_gradients, settings.Lambda, epoch);
                }

                //Invoke callback on batch complete
                await (on_batch_complete?.Invoke(model, token) ?? Task.CompletedTask);
            }

            return model;
        }
        catch(Exception e)
        {
            return Error(e.Message, e);
        }
    }

    public static DenseVector[] PropagateForwards(SubModel model, SSIMPredictionInput input)
    {
        DenseVector neurons = DenseVector.OfArray(input.NormalizedIntensities);

        DenseVector[] activations = new DenseVector[model.Weights.Length + 1];
        activations[0] = neurons;

        //For each layer, propagating forward
        for (int l = 0; l < model.Weights.Length; l++)
        {
            DenseMatrix layer = model.Weights[l];
            DenseVector bias = model.Biases[l];

            //Take dot product of weights and neurons, and add bias to get activation inputs
            DenseVector product = (layer * neurons) + bias;

            if (l == model.Weights.Length - 1)
            {
                //Apply output activation function (Sigmoid)
                product.MapInplace(value => 1 / (1 + Math.Exp(-value)));
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

    public static (DenseMatrix[] weight_gradients, DenseVector[] bias_gradients) PropagateBackwards(SubModel model, double expected_value, DenseVector[] activations)
    {
        DenseVector expected = DenseVector.OfArray([expected_value]);
        DenseMatrix[] all_gradients = new DenseMatrix[model.Weights.Length];
        DenseVector[] all_errors = new DenseVector[model.Weights.Length];
        DenseVector[] all_biases = new DenseVector[model.Weights.Length];

        for (int l = all_gradients.Length - 1; l >= 0; l--)
        {
            //Calculate the partial derivative of loss wrt to the weights of layer l
            DenseMatrix weights = model.Weights[l];
            DenseVector neurons = activations[l + 1];

            DenseVector[] layer_gradients = new DenseVector[weights.RowCount];
            DenseVector layer_bias = new(weights.RowCount);

            DenseVector error_term;

            if (l == all_gradients.Length - 1)
            {
                //Gradient of binary cross-entropy loss wrt sigmoid activation simplifies to predicted - expected
                error_term = neurons - expected;
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
    /// Initialize the weights matrix use He Initialization
    /// </summary>
    /// <param name="weights">The matrix to initialize</param>
    public static void InitializeWeightsHeNormal(double[,] weights)
    {
        int fan_in = weights.GetLength(1);
        double std_dev = Math.Sqrt(2d / fan_in);

        for (int i = 0; i < weights.GetLength(0); i++)
        {
            for (int j = 0; j < weights.GetLength(1); j++)
            {
                weights[i, j] = (Random.Shared.NextDouble() * std_dev * Math.Sqrt(12)) - (std_dev * Math.Sqrt(3));
            }
        }
    }

    /// <summary>
    /// Initialize the bias vector use He Initialization
    /// </summary>
    /// <param name="biases">The vector to initialize</param>
    public static void InitializeWeightsHeNormal(double[] biases)
    {
        double std_dev = Math.Sqrt(2d);

        for (int i = 0; i < biases.Length; i++)
        {
            biases[i] = (Random.Shared.NextDouble() * std_dev * Math.Sqrt(12)) - (std_dev * Math.Sqrt(3));
        }
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
    private static void UpdateAdamWeights(Adam adam, SubModel model, DenseMatrix[] gradients, DenseVector[] bias_gradients, double l2_coeff, int epoch)
    {
        for (int l = 0; l < model.Weights.Length; l++)
        {
            //Update first moment
            adam.Moment1[l] = adam.Decay.Beta1 * adam.Moment1[l] + (1 - adam.Decay.Beta1) * gradients[l];
            adam.Bias1[l] = adam.Decay.Beta1 * adam.Bias1[l] + (1 - adam.Decay.Beta1) * bias_gradients[l];

            //Update second moment
            adam.Moment2[l] = (DenseMatrix)(adam.Decay.Beta2 * adam.Moment2[l] + (1 - adam.Decay.Beta2) * gradients[l].PointwiseMultiply(gradients[l]));
            adam.Bias2[l] = (DenseVector)(adam.Decay.Beta2 * adam.Bias2[l] + (1 - adam.Decay.Beta2) * bias_gradients[l].PointwiseMultiply(bias_gradients[l]));

            //Compute bias-corrected first moment
            var moment1_corrected = adam.Moment1[l] / (1 - Math.Pow(adam.Decay.Beta1, epoch + 1));
            var bias1_corrected = adam.Bias1[l] / (1 - Math.Pow(adam.Decay.Beta1, epoch + 1));

            //Compute bias-corrected second moment
            var moment2_corrected = adam.Moment2[l] / (1 - Math.Pow(adam.Decay.Beta2, epoch + 1));
            var bias2_corrected = adam.Bias2[l] / (1 - Math.Pow(adam.Decay.Beta2, epoch + 1));

            var adam_learning_rate = moment1_corrected.PointwiseDivide(moment2_corrected.Map(m => Math.Sqrt(m) + ADAM_EPS));
            var adam_bias_learning_rate = bias1_corrected.PointwiseDivide(bias2_corrected.Map(m => Math.Sqrt(m) + ADAM_EPS));

            //L2 Regularization
            adam_learning_rate += l2_coeff * model.Weights[l];

            //Update weights and biases
            model.Weights[l] -= (DenseMatrix)(adam.LearningRate * adam_learning_rate);
            model.Biases[l] -= (DenseVector)(adam.LearningRate * adam_bias_learning_rate);
        }
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

    public class TrainingSettings
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

        public Adam(double learning_rate, double decay_1, double decay_2, SubModel model)
        {
            LearningRate = learning_rate;
            Decay = (decay_1, decay_2);

            Moment1 = new DenseMatrix[model.Weights.Length];
            Moment2 = new DenseMatrix[model.Weights.Length];
            Bias1 = new DenseVector[model.Weights.Length];
            Bias2 = new DenseVector[model.Weights.Length];

            //Init all values to 0
            for (int i = 0; i < model.Weights.Length; i++)
            {
                Moment1[i] = new DenseMatrix(model.Weights[i].RowCount, model.Weights[i].ColumnCount);
                Moment2[i] = new DenseMatrix(model.Weights[i].RowCount, model.Weights[i].ColumnCount);
                Bias1[i] = new DenseVector(model.Weights[i].RowCount);
                Bias2[i] = new DenseVector(model.Weights[i].RowCount);
            }
        }
    }

}

public class SSIMPredictionModel(SSIMPredictionModelInitParams init)
{
    /// <summary>
    /// All glyphs
    /// </summary>
    public string[] Glyphs { get; set; } = init.Glyphs.ToArray();

    /// <summary>
    /// Submodel for each glyph
    /// </summary>
    public Dictionary<string, SubModel> GlyphModels { get; set; } = init.Glyphs.ToDictionary(g => g, g => new SubModel(g, init));

    private SSIMPredictionModel() : this(new()) { }

    /// <summary>
    /// Encode model to bytes
    /// </summary>
    /// <returns></returns>
    public byte[] EncodeBytes()
    {
        using var ms = new MemoryStream();

        //Write number of glyphs
        ms.Write(BitConverter.GetBytes(Glyphs.Length));

        for(int g = 0; g < Glyphs.Length; g++)
        {
            var glyph_bytes = Encoding.UTF8.GetBytes(Glyphs[g]);

            //Write glyph length
            ms.Write(BitConverter.GetBytes(glyph_bytes.Length));

            //Write glyph
            ms.Write(glyph_bytes);
        }

        for (int g = 0; g < Glyphs.Length; g++)
        {
            var submodel = GlyphModels[Glyphs[g]];
            var subbytes = submodel.EncodeBytes();
            ms.Write(subbytes);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Decode model from bytes
    /// </summary>
    /// <param name="bytes"></param>
    /// <returns></returns>
    public static SSIMPredictionModel DecodeBytes(byte[] bytes)
    {
        List<string> glyphs = [];
        Dictionary<string, SubModel> glyph_models = [];

        int offset = 0;

        //Read number of glyphs
        var glyphs_count = BitConverter.ToInt32(bytes, offset);
        offset += sizeof(int);

        for(int g = 0; g < glyphs_count; g++)
        {
            //Get length of glyph
            var glyph_length = BitConverter.ToInt32(bytes, offset);
            offset += sizeof(int);

            //Get glyph
            var glyph = Encoding.UTF8.GetString(bytes, offset, glyph_length);
            offset += glyph_length;

            glyphs.Add(glyph);
        }

        for(int g = 0; g < glyphs_count; g++)
        {
            var submodel = SubModel.DecodeBytes(bytes, ref offset);
            glyph_models[submodel.Glyph] = submodel;
        }

        return new()
        {
            GlyphModels = glyph_models,
            Glyphs = [..glyphs]
        };
    }

    public SSIMPredictionModel Clone() => new()
    {
        GlyphModels = GlyphModels.ToDictionary(g => g.Key, g => g.Value.Clone()),
        Glyphs = [..Glyphs]
    };

    public class SubModel
    {
        /// <summary>
        /// The glyph this model predicts
        /// </summary>
        public string Glyph { get; set; } = string.Empty;

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

        private SubModel() { }

        public SubModel(string glyph, SSIMPredictionModelInitParams init)
        {
            Glyph = glyph;
            Alpha = init.Alpha;
            Weights = new DenseMatrix[1 + init.HiddenLayerCount];
            Biases = new DenseVector[1 + init.HiddenLayerCount];

            //Initialize weights
            for (int w = 0; w < Weights.Length; w++)
            {
                uint next_activations_count = w == Weights.Length - 1
                    ? 1
                    : init.HiddenLayerNeuronCount;

                uint neuron_count = w == 0
                    ? init.FeatureCount
                    : init.HiddenLayerNeuronCount;

                double[,] layer = new double[next_activations_count, neuron_count];
                SSIMPredictionTrainer.InitializeWeightsHeNormal(layer);
                Weights[w] = DenseMatrix.OfArray(layer);
            }

            //Initialize biases
            for (int b = 0; b < Biases.Length; b++)
            {
                uint next_activations_count = b == Biases.Length - 1
                    ? 1
                    : init.HiddenLayerNeuronCount;

                double[] layer = new double[next_activations_count];
                SSIMPredictionTrainer.InitializeWeightsHeNormal(layer);
                Biases[b] = DenseVector.OfArray(layer);
            }
        }

        /// <summary>
        /// Encode model from bytes
        /// </summary>
        /// <returns></returns>
        public byte[] EncodeBytes()
        {
            using var ms = new MemoryStream();
            var glyph_bytes = Encoding.UTF8.GetBytes(Glyph);

            //Write glyph length
            ms.Write(BitConverter.GetBytes(glyph_bytes.Length));

            //Write glyph
            ms.Write(glyph_bytes);

            //Write alpha
            ms.Write(BitConverter.GetBytes(Alpha));

            //Write number of layers
            ms.Write(BitConverter.GetBytes(Weights.Length));

            for (int w = 0; w < Weights.Length; w++)
            {
                DenseMatrix weights = Weights[w];
                DenseVector biases = Biases[w];

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
                for (int i = 0; i < biases.Count; i++)
                {
                    ms.Write(BitConverter.GetBytes(biases[i]));
                }
            }

            return ms.ToArray();
        }

        /// <summary>
        /// Decode model from bytes
        /// </summary>
        /// <param name="bytes"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static SubModel DecodeBytes(byte[] bytes, ref int offset)
        {
            //Get length of glyph
            var glyph_length = BitConverter.ToInt32(bytes, offset);
            offset += sizeof(int);

            //Get glyph
            var glyph = Encoding.UTF8.GetString(bytes, offset, glyph_length);
            offset += glyph_length;

            //Get alpha
            var alpha = BitConverter.ToDouble(bytes, offset);
            offset += sizeof(double);

            //Get number of layers
            int layers = BitConverter.ToInt32(bytes, offset);
            offset += sizeof(int);

            DenseMatrix[] weights = new DenseMatrix[layers];
            DenseVector[] biases = new DenseVector[layers];

            for (int l = 0; l < layers; l++)
            {
                //Get dimensions of layer
                int m = BitConverter.ToInt32(bytes, offset);
                offset += sizeof(int);
                int n = BitConverter.ToInt32(bytes, offset);
                offset += sizeof(int);

                double[,] layer = new double[m, n];

                //Read in weights for layer
                for (int i = 0; i < m; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        layer[i, j] = BitConverter.ToDouble(bytes, offset);
                        offset += sizeof(double);
                    }
                }

                weights[l] = DenseMatrix.OfArray(layer);

                double[] bias = new double[m];

                //Read in biases for layer
                for (int i = 0; i < m; i++)
                {
                    bias[i] = BitConverter.ToDouble(bytes, offset);
                    offset += sizeof(double);
                }

                biases[l] = DenseVector.OfArray(bias);
            }

            return new()
            {
                Glyph = glyph,
                Alpha = alpha,
                Biases = biases,
                Weights = weights
            };
        }

        public SubModel Clone() => new()
        {
            Glyph = Glyph,
            Alpha = Alpha,
            Weights = Weights.Select(w => (DenseMatrix)w.Clone()).ToArray(),
            Biases = Biases.Select(b => (DenseVector)b.Clone()).ToArray()
        };
    }

    public class SSIMPredictionModelInitParams
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
}

/// <summary>
/// A training example for the neural net
/// </summary>
public class SSIMPredictionInput
{
    private const double
        COERCE_TO_ZERO = 0.0001,
        MAX_DISTANCE = 0.03d;

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

            if (std_dev == 0 || double.IsNaN(mean) || double.IsNaN(std_dev))
                return Intensities;

            return [.. Intensities.Select(s => (s - mean) / std_dev)];
        }
    }

    /// <summary>
    /// The Structural Similarity Index between this image tile
    /// and each glyph.
    /// </summary>
    public double[] SSIMs { get; set; } = [];
}