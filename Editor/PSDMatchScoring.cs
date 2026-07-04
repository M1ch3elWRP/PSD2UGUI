using System;
using UnityEngine;

namespace PSDImporter
{
    public static class PSDMatchScoring
    {
        public struct ScoringInput
        {
            public PSDMatchGeometry.NodeGeom node;
            public PSDMatchGeometry.PsdGeom psd;
            /// <summary>
            /// 类型匹配分：100=完全匹配, 0&lt;x&lt;100=兼容匹配(如Text↔Image), 0=不匹配
            /// </summary>
            public float typeMatchScore;
            public float maxDistanceError;
            public float maxSizeDiff;
            public float weightPosition;
            public float weightSize;
            public float weightType;
            public float weightDepth;
            public float weightAnchor;
            public int maxDepthDiff;
            public float minAcceptScore;
            public bool enableGeometryReject;
            public float geometryRejectDistanceMultiplier;
            public float geometryRejectSizeRatio;
            /// <summary>父级亲和力加成乘数，默认1.0；父级已匹配时可设为>1的值（如1.2）</summary>
            public float parentAffinityMultiplier;

            public static ScoringInput FromConfig(PSDMatchGeometry.NodeGeom nodeGeom, PSDMatchGeometry.PsdGeom psdGeom, float typeScore, PSDImportConfig config, float parentAffinity = 1f)
            {
                return new ScoringInput
                {
                    node = nodeGeom,
                    psd = psdGeom,
                    typeMatchScore = typeScore,
                    maxDistanceError = config != null ? config.maxDistanceError : 0f,
                    maxSizeDiff = config != null ? config.maxSizeDiff : 0f,
                    weightPosition = config != null ? config.weightPosition : 0f,
                    weightSize = config != null ? config.weightSize : 0f,
                    weightType = config != null ? config.weightType : 0f,
                    weightDepth = config != null ? config.weightDepth : 0f,
                    weightAnchor = config != null ? config.weightAnchor : 0f,
                    maxDepthDiff = config != null ? config.maxDepthDiff : 10,
                    minAcceptScore = config != null ? config.minAcceptScore : 0f,
                    enableGeometryReject = config != null && config.enableGeometryReject,
                    geometryRejectDistanceMultiplier = config != null ? config.geometryRejectDistanceMultiplier : 1f,
                    geometryRejectSizeRatio = config != null ? config.geometryRejectSizeRatio : 1f,
                    parentAffinityMultiplier = parentAffinity
                };
            }

            public static ScoringInput FromConfigForItem(
                PSDMatchGeometry.NodeGeom nodeGeom,
                PSDMatchGeometry.PsdGeom psdGeom,
                float typeScore,
                PSDImportConfig config,
                PicData item,
                RectTransform node,
                float parentAffinity = 1f)
            {
                ScoringInput input = FromConfig(nodeGeom, psdGeom, typeScore, config, parentAffinity);
                if (!IsTextMatch(item, node))
                {
                    return input;
                }

                float sizeWeightMultiplier = config != null ? Mathf.Clamp01(config.textSizeScoreWeightMultiplier) : 0.35f;
                input.weightSize *= sizeWeightMultiplier;
                float textRejectRatio = config != null ? Mathf.Clamp01(config.textGeometryRejectSizeRatio) : 0.6f;
                input.geometryRejectSizeRatio = Mathf.Max(input.geometryRejectSizeRatio, textRejectRatio);
                return input;
            }

            private static bool IsTextMatch(PicData item, RectTransform node)
            {
                bool psdText = item.isText || string.Equals(item.uiType, "Text", StringComparison.OrdinalIgnoreCase);
                bool unityText = node != null && node.GetComponent<UILabel>() != null;
                return psdText || unityText;
            }
        }

        public struct GeometryBreakdown
        {
            public float distance;
            public float diffW;
            public float diffH;
            public float sizeDiff;
            public float sizeDiffRel;
            public float distNorm;
            public float sizeNorm;
            public float sameDepth;
            public float anchorDiff;
        }

        public struct ScoreBreakdown
        {
            public GeometryBreakdown geometry;
            public float scorePos;
            public float scoreSize;
            public float scoreType;
            public float scoreDepth;
            public float scoreAnchor;
            public float weightedPos;
            public float weightedSize;
            public float weightedType;
            public float weightedDepth;
            public float weightedAnchor;
            public float total;
            public bool passThresholds;
        }

        private const float AnchorNorm = 0.70710678f;
        private const float SoftThresholdMultiplier = 1.5f;

        public static GeometryBreakdown BuildGeometryBreakdown(PSDMatchGeometry.NodeGeom nodeGeom, PSDMatchGeometry.PsdGeom psdGeom, float maxDistanceError, float maxSizeDiff, int maxDepthDiff)
        {
            GeometryBreakdown geometry = default;
            geometry.distance = Vector2.Distance(nodeGeom.centerLocal, psdGeom.centerLocal);
            geometry.diffW = Mathf.Abs(nodeGeom.sizeLocal.x - psdGeom.sizeLocal.x);
            geometry.diffH = Mathf.Abs(nodeGeom.sizeLocal.y - psdGeom.sizeLocal.y);
            geometry.sizeDiff = geometry.diffW + geometry.diffH;
            float psdWidth = Mathf.Max(Mathf.Abs(psdGeom.sizeLocal.x), 1f);
            float psdHeight = Mathf.Max(Mathf.Abs(psdGeom.sizeLocal.y), 1f);
            geometry.sizeDiffRel = ((geometry.diffW / psdWidth) + (geometry.diffH / psdHeight)) * 0.5f;

            geometry.distNorm = maxDistanceError > 0f ? Mathf.Clamp01(geometry.distance / maxDistanceError) : 1f;
            geometry.sizeNorm = Mathf.Clamp01(geometry.sizeDiffRel);

            float depthNorm = maxDepthDiff > 0 ? Mathf.Clamp01(Mathf.Abs(psdGeom.depth - nodeGeom.depth) / (float)maxDepthDiff) : 1f;
            geometry.sameDepth = 1f - depthNorm;

            float anchorDist = Vector2.Distance(nodeGeom.anchorCenter, psdGeom.anchorCenter);
            geometry.anchorDiff = Mathf.Clamp01(anchorDist / AnchorNorm);
            return geometry;
        }

        public static ScoreBreakdown Evaluate(ScoringInput input)
        {
            ScoreBreakdown breakdown = default;
            breakdown.geometry = BuildGeometryBreakdown(input.node, input.psd, input.maxDistanceError, input.maxSizeDiff, input.maxDepthDiff);

            float softDistanceLimit = input.maxDistanceError > 0f ? input.maxDistanceError * SoftThresholdMultiplier : 0f;
            if (softDistanceLimit > 0f && breakdown.geometry.distance < softDistanceLimit)
            {
                breakdown.scorePos = (1f - (breakdown.geometry.distance / softDistanceLimit)) * 100f;
            }

            float sizeAbsLimit = input.maxSizeDiff > 0f ? input.maxSizeDiff * SoftThresholdMultiplier : 0f;
            bool sizePassesAbsoluteFuse = sizeAbsLimit <= 0f || breakdown.geometry.sizeDiff < sizeAbsLimit;
            if (sizePassesAbsoluteFuse)
            {
                breakdown.scoreSize = (1f - Mathf.Clamp01(breakdown.geometry.sizeDiffRel)) * 100f;
            }

            // typeMatchScore: 100=完全匹配, 0<x<100=兼容匹配(如Text↔Image替换), 0=不匹配
            breakdown.scoreType = Mathf.Clamp(input.typeMatchScore, 0f, 100f);
            breakdown.scoreDepth = Mathf.Clamp01(breakdown.geometry.sameDepth) * 100f;
            breakdown.scoreAnchor = (1f - Mathf.Clamp01(breakdown.geometry.anchorDiff)) * 100f;

            // parentAffinityMultiplier: 父级已匹配时对位置分乘以加成系数（默认1.0，无影响）
            float posMultiplier = input.parentAffinityMultiplier > 0f ? input.parentAffinityMultiplier : 1f;
            breakdown.weightedPos = breakdown.scorePos * input.weightPosition * posMultiplier;
            breakdown.weightedSize = breakdown.scoreSize * input.weightSize;
            breakdown.weightedType = breakdown.scoreType * input.weightType;
            breakdown.weightedDepth = breakdown.scoreDepth * input.weightDepth;
            breakdown.weightedAnchor = breakdown.scoreAnchor * input.weightAnchor;

            float total = breakdown.weightedPos + breakdown.weightedSize + breakdown.weightedType + breakdown.weightedDepth + breakdown.weightedAnchor;
            bool hasGeometryScore = breakdown.scorePos > 0f || breakdown.scoreSize > 0f;
            bool geometryRejected = input.enableGeometryReject &&
                                    input.maxDistanceError > 0f &&
                                    breakdown.geometry.distance > input.maxDistanceError * Mathf.Max(0.1f, input.geometryRejectDistanceMultiplier) &&
                                    breakdown.geometry.sizeDiffRel > Mathf.Clamp01(input.geometryRejectSizeRatio);
            breakdown.passThresholds = hasGeometryScore && !geometryRejected && total >= input.minAcceptScore;
            breakdown.total = breakdown.passThresholds ? total : 0f;
            return breakdown;
        }
    }
}
