using UnityEngine;
using UnityEngine.UI;

namespace PSDImporter
{
    public static class PSDMatchFeatureExtractor
    {
        public const int FeatureCount = 6;
        private const float AnchorNorm = 0.70710678f;

        public static float[] ExtractFeatures(PicData item, RectTransform node, Transform root, int psdWidth, int psdHeight, float maxDistanceError, float maxSizeDiff, int maxDepthDiff)
        {
            var features = new float[FeatureCount];
            if (node == null || root == null) return features;

            RectTransform rootRect = root as RectTransform;
            if (rootRect == null) return features;

            var nodeGeom = PSDMatchGeometry.ExtractNodeGeom(node, rootRect);
            var psdGeom = PSDMatchGeometry.BuildPsdGeom(item, psdWidth, psdHeight);

            float dist = Vector2.Distance(nodeGeom.centerLocal, psdGeom.centerLocal);
            float distNorm = (maxDistanceError > 0f) ? Mathf.Clamp01(dist / maxDistanceError) : 1f;

            float sizeDiff = Mathf.Abs(nodeGeom.sizeLocal.x - psdGeom.sizeLocal.x) + Mathf.Abs(nodeGeom.sizeLocal.y - psdGeom.sizeLocal.y);
            float sizeNorm = (maxSizeDiff > 0f) ? Mathf.Clamp01(sizeDiff / maxSizeDiff) : 1f;

            float typeMatch = IsTypeMatch(node, item.uiType) ? 1f : 0f;
            float inLayout = node.GetComponentInParent<LayoutGroup>() != null ? 1f : 0f;

            int depthPsd = psdGeom.depth;
            int depthNode = nodeGeom.depth;
            float depthNorm = maxDepthDiff > 0 ? Mathf.Clamp01(Mathf.Abs(depthPsd - depthNode) / (float)maxDepthDiff) : 1f;
            float sameDepth = 1f - depthNorm;

            float anchorDist = Vector2.Distance(nodeGeom.anchorCenter, psdGeom.anchorCenter);
            float anchorDiff = Mathf.Clamp01(anchorDist / AnchorNorm);

            features[0] = distNorm;
            features[1] = sizeNorm;
            features[2] = typeMatch;
            features[3] = inLayout;
            features[4] = sameDepth;
            features[5] = anchorDiff;

            return features;
        }

        public static float[] ExtractFeatures(PicData item, RectTransform node, Transform root, PSDData psdData, PSDMatchDataset dataset)
        {
            if (psdData == null || dataset == null) return new float[FeatureCount];
            return ExtractFeatures(item, node, root, psdData.width, psdData.height, dataset.maxDistanceError, dataset.maxSizeDiff, dataset.maxDepthDiff);
        }

        private static bool IsTypeMatch(Transform node, string psdType)
        {
            if (psdType == "Button") return node.GetComponent<Button>() != null;
            if (psdType == "Text") return node.GetComponent<Text>() != null;
            if (psdType == "RawImage") return node.GetComponent<RawImage>() != null;
            if (psdType == "Image") return node.GetComponent<Image>() != null && node.GetComponent<Button>() == null;
            if (psdType == "Layout") return node.GetComponent<LayoutGroup>() != null;
            if (psdType == "Item") return node.GetComponent<LayoutGroup>() == null && node.GetComponentInParent<LayoutGroup>() != null;
            return false;
        }
    }
}
