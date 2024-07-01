using System.Diagnostics.CodeAnalysis;

namespace LibI2A.Converter;

public class NeuralNetConverter(NeuralNetConverter.Model model) : IImageToASCIIConverter
{
    private const double EPS = 0.000000001d;

    private readonly Model model = model;

    public IEnumerable<(string glyph, uint? color)> ConvertImage(Stream stream)
    {
        throw new NotImplementedException();
    }

    public static bool Train(TrainingSet training_set, Action<string, bool> log, Action<DebugData> log_data, [NotNullWhen(true)] out Model? model, [NotNullWhen(false)] out string? error)
    {
        bool Error(string message, out string error)
        {
            log(message, true);
            error = message;
            return false;
        }

        model = null;
        error = null;

        //Verify that params are valid
        if (training_set.TrainingRate <= 0)
            return Error("Training rate must be positive.", out error);

        if (training_set.LearningRate <= 0)
            return Error("Learning rate must be positive.", out error);

        if (training_set.HiddenLayers < 0)
            return Error("Cannot have a negative number of hidden layers.", out error);

        if (training_set.FeatureSize < 1)
            return Error("Feature size must be positive.", out error);

        if(training_set.Glyphs.Length == 0)
            return Error("No glyphs were provided.", out error);

        var N = Math.Max(training_set.Glyphs.Length, training_set.FeatureSize) + 1;
        var M = training_set.Glyphs.Length;
        var O = training_set.HiddenLayers + 1;
        model = training_set.InitialModel ?? new(N, M, O, InitializeWeightsHeNormal);

        //Verify that the passed in initial model has the correct dimensions
        if (model.Weights.GetLength(0) != N
            || model.Weights.GetLength(1) != M
            || model.Weights.GetLength(2) != O)
            return Error("The provided model weights matrix is not of the correct dimensions. " + 
                $"Expected: {N}X{M}X{O}. Actual: {model.Weights.GetLength(0)}X{model.Weights.GetLength(1)}X{model.Weights.GetLength(2)}.", out error);

        log($"Training Rate: {training_set.TrainingRate}", false);
        log($"Learning Rate: {training_set.LearningRate}", false);
        log($"Hidden Layers: {training_set.HiddenLayers}", false);
        log($"Feature Count: {training_set.FeatureSize}", false);
        log($"Glyphs: {string.Join(", ", training_set.Glyphs)}", false);

        log("Starting training.", false);

        var training_data = training_set.TrainingData.GetEnumerator();

        for(int data_ndx = 0; training_data.MoveNext(); data_ndx++)
        {
            var data = training_data.Current;

            //Learning rate w/ exponential decay
            var learning_rate = training_set.LearningRate * Math.Exp(-training_set.LearningRateDecay * data_ndx);

            log($"Starting training on element {data_ndx} (learning rate: {learning_rate}).", false);

            //Skip invalid element
            if (data.Intensities.Length != training_set.FeatureSize)
            {
                log($"Element {data_ndx} has an incorrect feature count. "
                    + $"Expected: {training_set.FeatureSize}. Actual: {data.Intensities.Length}.", true);
                continue;
            }

            //Check if this tile should be trained on or not
            if (Random.Shared.NextDouble() > training_set.TrainingRate)
            {
                log($"Skipped training on element {data_ndx}.", false);
                continue;
            }

            double[] activations = [..data.Intensities];

            //Store values of previous activations
            var layers = new double[N, O];

            for (int i = 0; i < activations.Length; i++)
                layers[i, 0] = activations[i];

            log($"Propagating forwards on element {data_ndx}.", false);

            //Compute each layer, propagating forward
            for (int k = 0; k < model.Weights.GetLength(2); k++)
            {
                double[] next_activations = new double[training_set.Glyphs.Length];

                //Each activation is a linear combination of each weighted feature
                for (int j = 0; j < model.Weights.GetLength(1); j++)
                {
                    //Initial activation is the bias
                    var activation = model.Weights[model.Weights.GetLength(0) - 1, j, k];

                    for (int i = 0; i < activations.GetLength(0) - 1; i++)
                    {
                        activation += activations[i] * model.Weights[i, j, k];
                    }

                    //For hidden layers, use Rectified Linear Unit as activation function
                    //Max(0, x)
                    next_activations[j] = Math.Max(0, activation);
                }

                //Use results in next layer
                activations = next_activations;

                //Store values of previous activations
                for (int i = 0; i < activations.Length; i++)
                    layers[i, k] = activations[i];
            }

            //For output layer, use softmax as activation function
            //Softmax_i = e^z_i / sum[j = 1..K](e^z_j), where K = number of glyphs, z_n is the nth activation
            static double[] NormalizedSoftmax(double[] input)
            {
                var max = input.Max();
                var exp = input.Select(x => Math.Exp(x - max)).ToArray();
                var sum = exp.Sum();
                return exp.Select(x => x / sum).ToArray();
            }

            var output = NormalizedSoftmax(activations);

            //Also use softmax on training input
            var ssims = NormalizedSoftmax(data.SSIMs);

            //Calculate error using cross entropy loss -1 * sum[j = 1..K](e_j * log(p_j)), where K = number of glyphs,
            //e_n is the nth SSIM, and p_n is the nth predicted SSIM
            var cel = 0d;
            for (int i = 0; i < output.Length; i++)
            {
                var e = ssims[i];
                var p = output[i];
                cel -= e * Math.Log(p + EPS);
            }

            log($"Loss: {cel}. Propagating backwards on element {data_ndx}.", false);

            //Backpropagate
            double[] previous_gradients = new double[model.Weights.GetLength(1)];

            //Compute each layer, staring from last layer and moving backwards
            for (int k = model.Weights.GetLength(2) - 1; k >= 0; k--)
            {
                double[] current_gradients = new double[model.Weights.GetLength(1)];

                for (int j = 0; j < model.Weights.GetLength(1); j++)
                {
                    double gradient;

                    //Last layer; cross entropy loss partial derivative simplifies to predicted - expected
                    if (k == model.Weights.GetLength(2) - 1)
                    {
                        gradient = output[j] - ssims[j];
                    }
                    //Inner layer
                    else
                    {
                        //Chain rule; previous gradient * d/dx max(0, x) = previous gradient * (x <= 0? 0 : 1)
                        gradient = previous_gradients[j] * (layers[j, k + 1] > 0 ? 1 : 0);
                    }

                    //Update weight
                    for (int i = 0; i < model.Weights.GetLength(0) - 1; i++)
                        model.Weights[i, j, k] -= learning_rate * gradient * layers[i, k];

                    //Update bias
                    model.Weights[model.Weights.GetLength(0) - 1, j, k] -= learning_rate * gradient;

                    //Hold on to gradient to propagate it backwards
                    current_gradients[j] = gradient;
                }

                previous_gradients = current_gradients;

                log_data(new DebugData(model));
            }

            log($"Finished training on element {data_ndx}.", false);
        }

        log("Finished training.", false);

        return true;
    }

    private static void InitializeWeightsHeNormal(double[,,] weights)
    {
        var fan_in = weights.GetLength(1);
        var std_dev = Math.Sqrt(2d / fan_in);

        for(int i = 0; i < weights.GetLength(0); i++)
        {
            for(int j = 0; j < weights.GetLength(1); j++)
            {
                for(int k = 0; k < weights.GetLength(2); k++)
                {
                    weights[i, j, k] = Random.Shared.NextDouble() * std_dev * Math.Sqrt(12) - std_dev * Math.Sqrt(3);
                }
            }
        }
    }

    public class TrainingSet
    {
        public IEnumerable<Input> TrainingData { get; set; } = [];

        public string[] Glyphs { get; set; } = [];

        public int HiddenLayers { get; set; } = 0;

        public int FeatureSize { get; set; } = 0;

        public double LearningRate { get; set; } = 0;

        public double LearningRateDecay { get; set; } = 0;

        public double TrainingRate { get; set; } = 0;

        public Model? InitialModel { get; set; }
    }

    public class Model
    {
        public double[,,] Weights { get; set; }

        private Model() { Weights = new double[0, 0, 0]; }

        public Model(int m, int n, int o, Action<double[,,]> initialization_fn)
        {
            Weights = new double[m, n, o];          
            initialization_fn(Weights);
        }

        public byte[] ToBytes()
        {
            using var ms = new MemoryStream();

            //Write array dimensions
            ms.Write(BitConverter.GetBytes(Weights.GetLength(0)));
            ms.Write(BitConverter.GetBytes(Weights.GetLength(1)));
            ms.Write(BitConverter.GetBytes(Weights.GetLength(2)));

            //Write weights
            for(int i = 0; i < Weights.GetLength(0); i++)
            {
                for (int j = 0; j < Weights.GetLength(1); j++)
                {
                    for (int k = 0; k < Weights.GetLength(2); k++)
                    {
                        ms.Write(BitConverter.GetBytes(Weights[i, j, k]));
                    }
                }
            }

            return ms.GetBuffer();
        }

        public static Model FromBytes(byte[] bytes)
        {
            int offset = 0;

            //Get array dimensions
            int m = BitConverter.ToInt32(bytes, offset);
            offset += sizeof(int);
            int n = BitConverter.ToInt32(bytes, offset);
            offset += sizeof(int);
            int o = BitConverter.ToInt32(bytes, offset);
            offset += sizeof(int);

            var weights = new double[m, n, o];

            //Get weights
            for (int i = 0; i < weights.GetLength(0); i++)
            {
                for (int j = 0; j < weights.GetLength(1); j++)
                {
                    for (int k = 0; k < weights.GetLength(2); k++)
                    {
                        var next = BitConverter.ToDouble(bytes, offset);
                        offset += sizeof(double);
                        weights[i, j, k] = next;
                    }
                }
            }

            return new()
            {
                Weights = weights
            };
        }
    }

    public class Input
    {
        public double[] Intensities { get; set; } = [];

        public double[] SSIMs { get; set; } = [];
    }

    public class Output
    {
        public string PredictedGlyph { get; set; } = string.Empty;
    }

    public class DebugData(Model Model)
    {
        public Model Model { get; set; } = Model;
    }
}
