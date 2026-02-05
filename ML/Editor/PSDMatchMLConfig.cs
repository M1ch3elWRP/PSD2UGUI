using UnityEngine;

namespace PSDImporter
{
    [CreateAssetMenu(fileName = "PSDMatchMLConfig", menuName = "PSDTools/Match ML Config")]
    public class PSDMatchMLConfig : ScriptableObject
    {
        [Header("Auto Learn")]
        [Tooltip("Enable auto sample collection and incremental model updates.")]
        public bool autoLearnEnabled = false;

        [Tooltip("Allow repeated sampling for the same screen.")]
        public bool allowRepeatScreens = false;

        [Tooltip("Dataset JSON path (absolute or Assets-relative).")]
        public string datasetPath = "Assets/PSDTools/ML/psd_match_dataset.json";

        [Tooltip("Model JSON path (absolute or Assets-relative).")]
        public string modelPath = "Assets/PSDTools/ML/psd_match_model.json";

        [Header("Training")]
        [Tooltip("Negative samples per positive sample.")]
        public int negativePerPositive = 3;

        [Tooltip("Incremental training epochs per update.")]
        public int epochsPerUpdate = 30;

        [Tooltip("Learning rate for incremental updates.")]
        public float learningRate = 0.05f;

        [Tooltip("L2 regularization for incremental updates.")]
        public float l2 = 0.0001f;

        [Tooltip("Only record bindings that are confirmed.")]
        public bool requireConfirmed = true;

        [Tooltip("Max depth diff used for depth normalization.")]
        public int maxDepthDiff = 10;

        [Header("Runtime")]
        [Tooltip("Use ML model score instead of manual weights when matching.")]
        public bool useMlScore = false;
    }
}
