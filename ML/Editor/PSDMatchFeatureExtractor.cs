using UnityEngine;
using UnityEngine.UI;

namespace PSDImporter
{
    public static class PSDMatchFeatureExtractor
    {
        public const int FeatureCount = 6;
        public static float[] ExtractFeatures(PicData item, RectTransform node, Transform root, int psdWidth, int psdHeight, float maxDistanceError, float maxSizeDiff, int maxDepthDiff)
        {
            var features = new float[FeatureCount];
            if (node == null || root == null) return features;

            RectTransform rootRect = root as RectTransform;
            if (rootRect == null) return features;

            var nodeGeom = PSDMatchGeometry.ExtractNodeGeom(node, rootRect);
            var psdGeom = PSDMatchGeometry.BuildPsdGeom(item, psdWidth, psdHeight);

            bool typeMatch = IsTypeMatch(node, item.uiType);
            bool inLayout = node.GetComponentInParent<LayoutGroup>() != null;
            return ExtractFeaturesFromGeometry(nodeGeom, psdGeom, typeMatch, inLayout, maxDistanceError, maxSizeDiff, maxDepthDiff);
        }

        public static float[] ExtractFeaturesFromGeometry(
            PSDMatchGeometry.NodeGeom nodeGeom,
            PSDMatchGeometry.PsdGeom psdGeom,
            bool isTypeMatch,
            bool inLayout,
            float maxDistanceError,
            float maxSizeDiff,
            int maxDepthDiff)
        {
            var features = new float[FeatureCount];
            var geometry = PSDMatchScoring.BuildGeometryBreakdown(nodeGeom, psdGeom, maxDistanceError, maxSizeDiff, maxDepthDiff);

            features[0] = geometry.distNorm;
            features[1] = geometry.sizeNorm;
            features[2] = isTypeMatch ? 1f : 0f;
            features[3] = inLayout ? 1f : 0f;
            features[4] = geometry.sameDepth;
            features[5] = geometry.anchorDiff;
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
