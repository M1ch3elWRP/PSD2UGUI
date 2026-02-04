using UnityEngine;

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
    }
}
