using UnityEngine;
using UnityEditor;

namespace PSDImporter
{
    [CreateAssetMenu(fileName = "PSDImportConfig", menuName = "PSD2NGUI/Import Config")]
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
        [Range(0, 10)] public float weightDepth = 0.3f;
        [Range(0, 10)] public float weightAnchor = 0.1f;
        [Min(1)] public int maxDepthDiff = 10;

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

        [Tooltip("Override @Btn component. Must derive from UnityEngine.UI.Button.")]
        public MonoScript buttonComponent;

        [Tooltip("Override text component. Must derive from UnityEngine.UI.Text.")]
        public MonoScript textComponent;

        [Header("Text Font Override")]
        [Tooltip("If set, all generated/restored Text components use this font.")]
        public Font defaultTextFont;

        [Header("Text Layout Matching")]
        [Tooltip("Extra horizontal room added to generated/restored Text RectTransforms so Unity wrapping does not move the visible glyphs.")]
        [Min(0f)]
        public float textLayoutPaddingX = 24f;

        [Tooltip("Extra vertical room added to generated/restored Text RectTransforms.")]
        [Min(0f)]
        public float textLayoutPaddingY = 8f;

        [Tooltip("Keep an existing Text RectTransform when it is already larger than the PSD visual text bounds plus padding.")]
        public bool textPreserveLargerLayoutRect = true;

        [Tooltip("Only expand Text to its single-line preferred width when preferredWidth <= PSD width * this ratio. Larger text is treated as intentionally wrapped.")]
        [Min(1f)]
        public float textSingleLineExpansionMaxRatio = 1.25f;

        [Tooltip("Only expand Text to its single-line preferred width when the extra width is below this pixel threshold. Larger text is treated as intentionally wrapped.")]
        [Min(0f)]
        public float textSingleLineExpansionMaxExtraWidth = 96f;

        [Tooltip("Down-weight size mismatch for Text matching because Unity's layout box is intentionally larger than the PSD glyph bounds.")]
        [Range(0f, 1f)]
        public float textSizeScoreWeightMultiplier = 0.35f;

        [Tooltip("Size error ratio needed before Text candidates are rejected by geometry reject.")]
        [Range(0f, 1f)]
        public float textGeometryRejectSizeRatio = 0.6f;

        [Header("Layout Overrides (optional)")]
        [Tooltip("Override @H layout group. Must derive from UnityEngine.UI.HorizontalLayoutGroup.")]
        public MonoScript horizontalLayoutComponent;

        [Tooltip("Override @V layout group. Must derive from UnityEngine.UI.VerticalLayoutGroup.")]
        public MonoScript verticalLayoutComponent;

        [Tooltip("Override @G layout group. Must derive from UnityEngine.UI.GridLayoutGroup.")]
        public MonoScript gridLayoutComponent;

        [Header("Matching Filters")]
        [Tooltip("Use saved PSDBindingData layer-ID history before scoring. Disable during audit/tuning so stale bindings do not override the matcher.")]
        public bool enableIdHistoryMatch = false;

        [Tooltip("When enabled, inactive prefab nodes are ignored during auto match.")]
        public bool skipInactiveMatch = false;

        [Tooltip("Allow items to stay unmatched by assigning them to dummy slots.")]
        public bool allowUnmatched = true;

        [Tooltip("Score threshold used as dummy match cost when allowUnmatched is enabled.")]
        [Min(0f)]
        public float unmatchedPenalty = 120f;

        [Tooltip("Minimum acceptable score for a real node assignment. Lower scores are marked unmatched.")]
        [Min(0f)]
        public float minAcceptScore = 1f;

        [Header("Geometry Reject")]
        [Tooltip("Reject candidates whose center and size are both clearly wrong, even if their type score is high.")]
        public bool enableGeometryReject = true;

        [Tooltip("Reject when distance > maxDistanceError * this multiplier and size error also exceeds geometryRejectSizeRatio.")]
        [Min(0.1f)]
        public float geometryRejectDistanceMultiplier = 1f;

        [Tooltip("Average relative width/height error needed for geometry rejection.")]
        [Range(0f, 1f)]
        public float geometryRejectSizeRatio = 0.25f;

        [Header("Candidate Pruning")]
        [Tooltip("Skip candidates that are clearly too far away before Hungarian assignment.")]
        public bool enableCandidatePruning = true;

        [Tooltip("Spatial prune radius = maxDistanceError * this multiplier.")]
        [Min(0.1f)]
        public float candidateDistanceMultiplier = 2.5f;

        [Header("Type Compatibility Matching")]
        [Tooltip("兼容类型匹配分（0~100）。当 PSD 类型与白膜组件类型不同但可互换时（如 Text↔Image 美术字替换），使用此分数代替 0。\n设为 0 则禁用兼容匹配（保持原有严格类型过滤行为）。推荐值: 40")]
        [Range(0f, 100f)]
        public float typeCompatScore = 40f;

        [Header("Parent Affinity Bonus")]
        [Tooltip("父级亲和力加成。当 PSD 项的父节点已匹配到某个白膜节点时，对该白膜节点的子节点匹配位置分乘以此倍数。\n设为 1.0 则禁用加成。推荐值: 1.3~1.5（防止子节点跑到其他父节点下）")]
        [Range(1f, 3f)]
        public float parentAffinityBonus = 1.3f;

        [Tooltip("Position-score multiplier for descendants of an already matched PSD parent.")]
        [Range(1f, 3f)]
        public float hierarchyDescendantAffinityBonus = 1.1f;

        [Tooltip("Total-score multiplier for candidates outside an already matched PSD parent.")]
        [Range(0f, 1f)]
        public float hierarchyOutsideParentPenalty = 0.85f;

        [Header("Auto Create Unmatched Nodes")]
        [Tooltip("当 PSD 中有节点在白膜中找不到匹配时（装饰图、新增节点），自动在对应父节点下创建子节点。\n关闭则保持原有行为（仅标记 Unmatched，不创建）。")]
        public bool autoCreateUnmatched = true;

        [Header("Layered Matching (Phase 3.5 Pre-Lock)")]
        [Tooltip("Enable high-confidence pre-lock before Hungarian algorithm to prevent global-optimal misassignment.\nWhen enabled, match pairs exceeding threshold with exclusive-optimal detection are locked before Hungarian runs.")]
        public bool preLockEnabled = true;

        [Tooltip("Minimum score for a match pair to be considered high-confidence pre-lockable.\nPairs scoring above this AND passing exclusive-optimal check will be pre-locked before Hungarian.\nRecommended: higher than perfectThreshold (150), e.g. 200.")]
        [Min(0f)]
        public float preLockThreshold = 200f;

        [Tooltip("Column uniqueness ratio (0.5~1). A node is 'contested' if another PSD row scores >= bestScore * ratio on the same column.\nLower value = stricter (less likely to pre-lock). Recommended: 0.80~0.90.")]
        [Range(0.5f, 1f)]
        public float preLockColumnUniquenessRatio = 0.85f;

        [Tooltip("Use score matrix distribution to derive the pre-lock threshold.")]
        public bool preLockUseDynamicThreshold = true;

        [Tooltip("Dynamic threshold = max(matrixMaxScore * ratio, dynamic min threshold).")]
        [Range(0.1f, 1f)]
        public float preLockDynamicRatio = 0.7f;

        [Tooltip("Minimum score for dynamic pre-lock threshold.")]
        [Min(0f)]
        public float preLockDynamicMinThreshold = 180f;

        [Tooltip("Minimum row gap between best and second-best candidate for pre-lock.")]
        [Min(0f)]
        public float preLockMinScoreGap = 15f;

        [Header("Match Confidence")]
        [Tooltip("A match with score gap below this value is shown as low confidence and will not auto-confirm.")]
        [Min(0f)]
        public float lowConfidenceMargin = 15f;

        [Header("Debug Options")]
        [Tooltip("Always print Top5 candidates even when all items are matched by ID.")]
        public bool forceCandidateLog = false;

        [Tooltip("Print node/PSD root-local geometry (center/size) during match scoring for debugging anchor/pivot/scale/layout impact.")]
        public bool logMatchGeometry = false;

    }
}
