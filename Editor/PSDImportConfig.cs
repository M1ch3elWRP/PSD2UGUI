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
    }
}
