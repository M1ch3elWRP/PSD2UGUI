using UnityEngine;

namespace PSDImporter
{
    public static class PSDMatchScoring
    {
        public struct ScoringInput
        {
            public PSDMatchGeometry.NodeGeom node;
            public PSDMatchGeometry.PsdGeom psd;
            public bool isTypeMatch;
            public float maxDistanceError;
            public float maxSizeDiff;
            public float weightPosition;
            public float weightSize;
            public float weightType;
            public int maxDepthDiff;

            public static ScoringInput FromConfig(PSDMatchGeometry.NodeGeom nodeGeom, PSDMatchGeometry.PsdGeom psdGeom, bool typeMatch, PSDImportConfig config)
            {
                return new ScoringInput
                {
                    node = nodeGeom,
                    psd = psdGeom,
                    isTypeMatch = typeMatch,
                    maxDistanceError = config != null ? config.maxDistanceError : 0f,
                    maxSizeDiff = config != null ? config.maxSizeDiff : 0f,
                    weightPosition = config != null ? config.weightPosition : 0f,
                    weightSize = config != null ? config.weightSize : 0f,
                    weightType = config != null ? config.weightType : 0f,
                    maxDepthDiff = config != null && config.mlConfig != null ? config.mlConfig.maxDepthDiff : 10
                };
            }
        }

        public struct GeometryBreakdown
        {
            public float distance;
            public float diffW;
            public float diffH;
            public float sizeDiff;
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
            public float weightedPos;
            public float weightedSize;
            public float weightedType;
            public float total;
            public bool passThresholds;
        }

        private const float AnchorNorm = 0.70710678f;

        public static GeometryBreakdown BuildGeometryBreakdown(PSDMatchGeometry.NodeGeom nodeGeom, PSDMatchGeometry.PsdGeom psdGeom, float maxDistanceError, float maxSizeDiff, int maxDepthDiff)
        {
            GeometryBreakdown geometry = default;
            geometry.distance = Vector2.Distance(nodeGeom.centerLocal, psdGeom.centerLocal);
            geometry.diffW = Mathf.Abs(nodeGeom.sizeLocal.x - psdGeom.sizeLocal.x);
            geometry.diffH = Mathf.Abs(nodeGeom.sizeLocal.y - psdGeom.sizeLocal.y);
            geometry.sizeDiff = geometry.diffW + geometry.diffH;

            geometry.distNorm = maxDistanceError > 0f ? Mathf.Clamp01(geometry.distance / maxDistanceError) : 1f;
            geometry.sizeNorm = maxSizeDiff > 0f ? Mathf.Clamp01(geometry.sizeDiff / maxSizeDiff) : 1f;

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

            if (input.maxDistanceError > 0f && breakdown.geometry.distance < input.maxDistanceError)
            {
                breakdown.scorePos = (1f - (breakdown.geometry.distance / input.maxDistanceError)) * 100f;
            }

            if (input.maxSizeDiff > 0f && breakdown.geometry.sizeDiff < input.maxSizeDiff)
            {
                breakdown.scoreSize = (1f - (breakdown.geometry.sizeDiff / input.maxSizeDiff)) * 100f;
            }

            breakdown.scoreType = input.isTypeMatch ? 100f : 0f;
            breakdown.passThresholds = breakdown.scorePos > 0f || breakdown.scoreSize > 0f;

            breakdown.weightedPos = breakdown.scorePos * input.weightPosition;
            breakdown.weightedSize = breakdown.scoreSize * input.weightSize;
            breakdown.weightedType = breakdown.scoreType * input.weightType;
            breakdown.total = breakdown.passThresholds ? (breakdown.weightedPos + breakdown.weightedSize + breakdown.weightedType) : 0f;
            return breakdown;
        }
    }
}
