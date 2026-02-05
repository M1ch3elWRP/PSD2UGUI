using System;
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

            float localX = item.x - psdWidth * 0.5f;
            float localY = item.y - psdHeight * 0.5f;
            Vector3 targetWorldPos = root.TransformPoint(new Vector3(localX, localY, 0));

            float dist = Vector3.Distance(node.position, targetWorldPos);
            float distNorm = (maxDistanceError > 0f) ? Mathf.Clamp01(dist / maxDistanceError) : 1f;

            float sizeDiff = Mathf.Abs(node.rect.width - item.width) + Mathf.Abs(node.rect.height - item.height);
            float sizeNorm = (maxSizeDiff > 0f) ? Mathf.Clamp01(sizeDiff / maxSizeDiff) : 1f;

            float typeMatch = IsTypeMatch(node, item.uiType) ? 1f : 0f;
            float inLayout = node.GetComponentInParent<LayoutGroup>() != null ? 1f : 0f;

            int depthPsd = GetPsdDepth(item.groupName);
            int depthNode = GetNodeDepth(node, root);
            float depthNorm = maxDepthDiff > 0 ? Mathf.Clamp01(Mathf.Abs(depthPsd - depthNode) / (float)maxDepthDiff) : 1f;
            float sameDepth = 1f - depthNorm;

            Vector2 anchorCenter = (node.anchorMin + node.anchorMax) * 0.5f;
            float anchorDist = Vector2.Distance(anchorCenter, new Vector2(0.5f, 0.5f));
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

        private static int GetPsdDepth(string groupName)
        {
            if (string.IsNullOrEmpty(groupName)) return 0;
            string trimmed = groupName.Trim('/');
            if (string.IsNullOrEmpty(trimmed)) return 0;
            return trimmed.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private static int GetNodeDepth(Transform node, Transform root)
        {
            int depth = 0;
            var current = node;
            while (current != null && current != root)
            {
                depth++;
                current = current.parent;
            }
            return depth;
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
