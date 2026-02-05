using UnityEngine;
using UnityEditor;

namespace PSDImporter
{
    [CreateAssetMenu(fileName = "PSDImportConfig", menuName = "PSDTools/Import Config")]
    public class PSDImportConfig : ScriptableObject
    {
        [Header("评分阈值 (超过此值该项得分为0)")]
        [Tooltip("最大允许的位置偏差 (像素)")]
        public float maxDistanceError = 200f;

        [Tooltip("最大允许的尺寸偏差 (宽+高差异像素)")]
        public float maxSizeDiff = 100f;

        [Header("评分权重 (总分 = 各项得分 * 权重)")]
        [Range(0, 10)] public float weightPosition = 1.0f; // 位置权重
        [Range(0, 10)] public float weightSize = 0.8f;     // 尺寸权重 (建议比位置略低)
        [Range(0, 10)] public float weightType = 2.0f;     // 类型权重 (类型对不上通常就是错的)

        [Header("调试")]
        public bool showDetailedLog = true;

        [Header("Auto 9-Slice")]
        [Tooltip("Detect large uniform borders from PNGs and set sprite borders.")]
        public bool autoSlice = false;

        [Header("Asset Deduplication")]
        [Tooltip("Reuse identical PNGs by content hash when assigning sprites/textures.")]
        public bool dedupeSprites = false;
        [Tooltip("Move duplicate PNG files into a subfolder (do not delete).")]
        public bool dedupeMoveDuplicates = false;
        [Tooltip("Subfolder name under PSD asset folder for moved duplicates.")]
        public string dedupeMoveFolder = "_Duplicates";

        [Header("Common Sprite Matching")]
        [Tooltip("Enable @Common layer matching against project sprites.")]
        public bool commonSpriteMatch = false;

        [Tooltip("Folders under Assets that contain common sprites (used for @Common matching).")]
        public string[] commonSpriteFolders = new string[0];

        [Tooltip("Perceptual hash threshold for @Common fallback (0 to disable).")]
        public int commonSpritePerceptualThreshold = 8;

        [Tooltip("Move matched @Common exports into a subfolder for manual cleanup.")]
        public bool commonSpriteMoveMatched = true;

        [Tooltip("Subfolder name under PSD asset folder for matched @Common exports.")]
        public string commonSpriteMoveFolder = "_CommonMatched";

        [Header("Component Overrides (optional)")]
        [Tooltip("Override @Img/@Image component. Must derive from UnityEngine.UI.Image.")]
        public MonoScript imageComponent;

        [Tooltip("Override @Bg component. Must derive from UnityEngine.UI.RawImage.")]
        public MonoScript rawImageComponent;

        [Tooltip("Override @Btn component. Must derive from UnityEngine.UI.Button.")]
        public MonoScript buttonComponent;

        [Tooltip("Override text component. Must derive from UnityEngine.UI.Text.")]
        public MonoScript textComponent;

        [Header("Text Font Override")]
        [Tooltip("If set, all generated/restored Text components use this font.")]
        public Font defaultTextFont;

        [Header("Layout Overrides (optional)")]
        [Tooltip("Override @H layout group. Must derive from UnityEngine.UI.HorizontalLayoutGroup.")]
        public MonoScript horizontalLayoutComponent;

        [Tooltip("Override @V layout group. Must derive from UnityEngine.UI.VerticalLayoutGroup.")]
        public MonoScript verticalLayoutComponent;

        [Tooltip("Override @G layout group. Must derive from UnityEngine.UI.GridLayoutGroup.")]
        public MonoScript gridLayoutComponent;

        [Header("Matching Filters")]
        [Tooltip("When enabled, inactive prefab nodes are ignored during auto match.")]
        public bool skipInactiveMatch = false;

        [Header("Debug Options")]
        [Tooltip("Always print Top5 candidates even when all items are matched by ID.")]
        public bool forceCandidateLog = false;

        [Header("Auto Learn (ML)")]
        [Tooltip("Enable auto sample collection and incremental model updates.")]
        public bool autoLearnEnabled = false;

        [Tooltip("Dataset JSON path (absolute or Assets-relative).")]
        public string autoLearnDatasetPath = "Assets/PSDTools/ML/psd_match_dataset.json";

        [Tooltip("Model JSON path (absolute or Assets-relative).")]
        public string autoLearnModelPath = "Assets/PSDTools/ML/psd_match_model.json";

        [Tooltip("Negative samples per positive sample.")]
        public int autoLearnNegativePerPositive = 3;

        [Tooltip("Incremental training epochs per update.")]
        public int autoLearnEpochsPerUpdate = 30;

        [Tooltip("Learning rate for incremental updates.")]
        public float autoLearnLearningRate = 0.05f;

        [Tooltip("L2 regularization for incremental updates.")]
        public float autoLearnL2 = 0.0001f;

        [Tooltip("Only record bindings that are confirmed.")]
        public bool autoLearnRequireConfirmed = true;

        [Tooltip("Max depth diff used for depth normalization.")]
        public int autoLearnMaxDepthDiff = 10;

        [Tooltip("Use ML model score instead of manual weights when matching.")]
        public bool useMlScore = false;
    }
}
