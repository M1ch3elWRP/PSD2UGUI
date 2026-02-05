using System;

namespace PSDImporter
{
    [Serializable]
    public class PSDMatchSample
    {
        public float[] x;
        public int y;
        public string item;
        public string node;
        public string screenId;
    }

    [Serializable]
    public class PSDMatchDataset
    {
        public int featureCount = 6;
        public float maxDistanceError = 200f;
        public float maxSizeDiff = 100f;
        public int maxDepthDiff = 10;
        public string[] screenIds = new string[0];
        public PSDMatchSample[] samples = new PSDMatchSample[0];
    }

    [Serializable]
    public class PSDMatchModel
    {
        public int featureCount = 6;
        public float[] weights;
        public float bias;
        public float maxDistanceError = 200f;
        public float maxSizeDiff = 100f;
        public int maxDepthDiff = 10;
        public float trainingLoss;
        public float trainingAccuracy;
        public string trainedAtUtc;
    }

    [Serializable]
    public class PSDMatchTrainingOptions
    {
        public int epochs = 300;
        public float learningRate = 0.1f;
        public float l2 = 0.0001f;
    }

    public struct PSDMatchTrainingReport
    {
        public int sampleCount;
        public float loss;
        public float accuracy;
        public int epochs;
    }

    public static class PSDMatchML
    {
        public static PSDMatchModel Train(PSDMatchDataset dataset, PSDMatchTrainingOptions options, out PSDMatchTrainingReport report)
        {
            report = new PSDMatchTrainingReport();
            if (dataset == null || dataset.samples == null || dataset.samples.Length == 0)
            {
                throw new ArgumentException("Dataset is empty.");
            }

            int featureCount = dataset.featureCount;
            if (featureCount <= 0)
            {
                throw new ArgumentException("Invalid featureCount.");
            }

            var weights = new float[featureCount];
            float bias = 0f;
            int sampleCount = 0;

            for (int epoch = 0; epoch < options.epochs; epoch++)
            {
                float loss = 0f;
                int correct = 0;
                sampleCount = 0;

                for (int i = 0; i < dataset.samples.Length; i++)
                {
                    var s = dataset.samples[i];
                    if (s == null || s.x == null || s.x.Length != featureCount) continue;
                    int y = s.y != 0 ? 1 : 0;

                    float z = bias;
                    for (int k = 0; k < featureCount; k++) z += weights[k] * s.x[k];
                    float p = Sigmoid(z);

                    float diff = p - y;
                    for (int k = 0; k < featureCount; k++)
                    {
                        float grad = diff * s.x[k] + options.l2 * weights[k];
                        weights[k] -= options.learningRate * grad;
                    }
                    bias -= options.learningRate * diff;

                    float eps = 1e-6f;
                    loss += (float)(-(y * Math.Log(p + eps) + (1 - y) * Math.Log(1 - p + eps)));
                    if ((p >= 0.5f) == (y == 1)) correct++;
                    sampleCount++;
                }

                report.loss = sampleCount > 0 ? loss / sampleCount : 0f;
                report.accuracy = sampleCount > 0 ? (float)correct / sampleCount : 0f;
            }

            report.sampleCount = sampleCount;
            report.epochs = options.epochs;

            return new PSDMatchModel
            {
                featureCount = featureCount,
                weights = weights,
                bias = bias,
                maxDistanceError = dataset.maxDistanceError,
                maxSizeDiff = dataset.maxSizeDiff,
                maxDepthDiff = dataset.maxDepthDiff,
                trainingLoss = report.loss,
                trainingAccuracy = report.accuracy,
                trainedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };
        }

        public static PSDMatchModel TrainIncremental(PSDMatchSample[] samples, PSDMatchDataset dataset, PSDMatchTrainingOptions options, PSDMatchModel startModel, out PSDMatchTrainingReport report)
        {
            report = new PSDMatchTrainingReport();
            if (samples == null || samples.Length == 0)
            {
                return startModel;
            }

            int featureCount = dataset.featureCount;
            if (featureCount <= 0) throw new ArgumentException("Invalid featureCount.");

            float[] weights = new float[featureCount];
            float bias = 0f;
            if (startModel != null && startModel.weights != null && startModel.weights.Length == featureCount)
            {
                Array.Copy(startModel.weights, weights, featureCount);
                bias = startModel.bias;
            }

            int sampleCount = 0;
            for (int epoch = 0; epoch < options.epochs; epoch++)
            {
                float loss = 0f;
                int correct = 0;
                sampleCount = 0;

                for (int i = 0; i < samples.Length; i++)
                {
                    var s = samples[i];
                    if (s == null || s.x == null || s.x.Length != featureCount) continue;
                    int y = s.y != 0 ? 1 : 0;

                    float z = bias;
                    for (int k = 0; k < featureCount; k++) z += weights[k] * s.x[k];
                    float p = Sigmoid(z);

                    float diff = p - y;
                    for (int k = 0; k < featureCount; k++)
                    {
                        float grad = diff * s.x[k] + options.l2 * weights[k];
                        weights[k] -= options.learningRate * grad;
                    }
                    bias -= options.learningRate * diff;

                    float eps = 1e-6f;
                    loss += (float)(-(y * Math.Log(p + eps) + (1 - y) * Math.Log(1 - p + eps)));
                    if ((p >= 0.5f) == (y == 1)) correct++;
                    sampleCount++;
                }

                report.loss = sampleCount > 0 ? loss / sampleCount : 0f;
                report.accuracy = sampleCount > 0 ? (float)correct / sampleCount : 0f;
            }

            report.sampleCount = sampleCount;
            report.epochs = options.epochs;

            return new PSDMatchModel
            {
                featureCount = featureCount,
                weights = weights,
                bias = bias,
                maxDistanceError = dataset.maxDistanceError,
                maxSizeDiff = dataset.maxSizeDiff,
                maxDepthDiff = dataset.maxDepthDiff,
                trainingLoss = report.loss,
                trainingAccuracy = report.accuracy,
                trainedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };
        }

        public static float Predict(PSDMatchModel model, float[] x)
        {
            if (model == null || x == null || x.Length != model.featureCount) return 0f;
            float z = model.bias;
            for (int k = 0; k < model.featureCount; k++) z += model.weights[k] * x[k];
            return Sigmoid(z);
        }

        private static float Sigmoid(float z)
        {
            if (z >= 0)
            {
                float ez = (float)Math.Exp(-z);
                return 1f / (1f + ez);
            }
            else
            {
                float ez = (float)Math.Exp(z);
                return ez / (1f + ez);
            }
        }
    }
}
