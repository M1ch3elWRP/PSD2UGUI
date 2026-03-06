using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace PSDImporter
{
    public static class PSDCandidateBuilder
    {
        public struct FilterReasonCount
        {
            public int type;
            public int depth;
            public int distance;
            public int size;
            public int layout;
        }

        public class BuildResult
        {
            public readonly List<RectTransform> candidates = new List<RectTransform>();
            public FilterReasonCount filtered;
        }

        private const int MaxDepthDiff = 2;
        private static readonly bool EnableDepthFilter = false;
        private const float MaxCenterDistanceScale = 1.25f;
        private const float MaxSizeRatio = 3.0f;
        private const float MinIoU = 0.01f;

        public static BuildResult BuildCandidates(
            PicData item,
            IList<RectTransform> nodes,
            Transform rootTransform,
            PSDData cachedPsdData,
            PSDImportConfig config)
        {
            BuildResult result = new BuildResult();
            if (nodes == null || rootTransform == null || config == null)
            {
                return result;
            }

            Rect itemRect = GetItemRootLocalRect(item, cachedPsdData);
            int psdDepth = GetPsdGroupDepth(item.groupName);
            string layoutContextName = GetLayoutContextName(item, cachedPsdData);

            List<RectTransform> provisional = new List<RectTransform>();
            List<RectTransform> sameLayout = new List<RectTransform>();

            for (int i = 0; i < nodes.Count; i++)
            {
                RectTransform node = nodes[i];
                if (!IsTypeMatch(node, item.uiType))
                {
                    result.filtered.type++;
                    continue;
                }

                if (EnableDepthFilter)
                {
                    int nodeDepth = GetNodeDepth(node, rootTransform);
                    if (Mathf.Abs(nodeDepth - psdDepth) > MaxDepthDiff)
                    {
                        result.filtered.depth++;
                        continue;
                    }
                }

                Rect nodeRect = GetNodeRootLocalRect(node, rootTransform);
                float centerDistance = Vector2.Distance(itemRect.center, nodeRect.center);
                if (centerDistance > config.maxDistanceError * MaxCenterDistanceScale)
                {
                    result.filtered.distance++;
                    continue;
                }

                if (!IsSizeAndOverlapValid(itemRect, nodeRect, config.maxSizeDiff))
                {
                    result.filtered.size++;
                    continue;
                }

                provisional.Add(node);
                if (!string.IsNullOrEmpty(layoutContextName))
                {
                    string nodeLayout = GetNearestLayoutContainerName(node, rootTransform);
                    if (string.Equals(nodeLayout, layoutContextName, StringComparison.OrdinalIgnoreCase))
                    {
                        sameLayout.Add(node);
                    }
                }
            }

            if (!string.IsNullOrEmpty(layoutContextName) && sameLayout.Count > 0)
            {
                result.filtered.layout = provisional.Count - sameLayout.Count;
                result.candidates.AddRange(sameLayout);
            }
            else
            {
                result.candidates.AddRange(provisional);
            }

            return result;
        }

        public static bool IsTypeMatch(Transform node, string psdType)
        {
            if (psdType == "Button") return node.GetComponent<Button>() != null;
            if (psdType == "Text") return node.GetComponent<Text>() != null;
            if (psdType == "RawImage") return node.GetComponent<RawImage>() != null;
            if (psdType == "Image") return node.GetComponent<Image>() != null && node.GetComponent<Button>() == null;
            if (psdType == "Layout") return node.GetComponent<LayoutGroup>() != null;
            if (psdType == "Item") return node.GetComponent<LayoutGroup>() == null && node.GetComponentInParent<LayoutGroup>() != null;
            return false;
        }

        private static Rect GetItemRootLocalRect(PicData item, PSDData data)
        {
            float x = item.x - data.width * 0.5f;
            float y = item.y - data.height * 0.5f;
            return new Rect(x - item.width * 0.5f, y - item.height * 0.5f, item.width, item.height);
        }

        private static Rect GetNodeRootLocalRect(RectTransform node, Transform root)
        {
            Vector3[] corners = new Vector3[4];
            node.GetWorldCorners(corners);
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < 4; i++)
            {
                Vector3 local = root.InverseTransformPoint(corners[i]);
                min = Vector2.Min(min, local);
                max = Vector2.Max(max, local);
            }

            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private static bool IsSizeAndOverlapValid(Rect itemRect, Rect nodeRect, float maxSizeDiff)
        {
            float w1 = Mathf.Max(1f, itemRect.width);
            float h1 = Mathf.Max(1f, itemRect.height);
            float w2 = Mathf.Max(1f, nodeRect.width);
            float h2 = Mathf.Max(1f, nodeRect.height);

            float ratioW = Mathf.Max(w1, w2) / Mathf.Min(w1, w2);
            float ratioH = Mathf.Max(h1, h2) / Mathf.Min(h1, h2);
            float diff = Mathf.Abs(w1 - w2) + Mathf.Abs(h1 - h2);
            if (ratioW > MaxSizeRatio || ratioH > MaxSizeRatio || diff > maxSizeDiff * 2f)
            {
                return false;
            }

            float iou = CalculateIoU(itemRect, nodeRect);
            return iou >= MinIoU || itemRect.Overlaps(nodeRect);
        }

        private static float CalculateIoU(Rect a, Rect b)
        {
            float xMin = Mathf.Max(a.xMin, b.xMin);
            float yMin = Mathf.Max(a.yMin, b.yMin);
            float xMax = Mathf.Min(a.xMax, b.xMax);
            float yMax = Mathf.Min(a.yMax, b.yMax);
            float interW = Mathf.Max(0f, xMax - xMin);
            float interH = Mathf.Max(0f, yMax - yMin);
            float intersection = interW * interH;
            if (intersection <= 0f)
            {
                return 0f;
            }

            float union = a.width * a.height + b.width * b.height - intersection;
            if (union <= 0f)
            {
                return 0f;
            }

            return intersection / union;
        }

        private static int GetPsdGroupDepth(string groupName)
        {
            if (string.IsNullOrEmpty(groupName))
            {
                return 0;
            }

            string[] parts = groupName.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            int depth = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (!string.Equals(parts[i], "root", StringComparison.OrdinalIgnoreCase))
                {
                    depth++;
                }
            }
            return depth;
        }

        private static int GetNodeDepth(Transform node, Transform root)
        {
            int depth = 0;
            Transform current = node;
            while (current != null && current != root)
            {
                depth++;
                current = current.parent;
            }
            return Mathf.Max(0, depth - 1);
        }

        private static string GetLayoutContextName(PicData item, PSDData data)
        {
            if (data == null || data.listPngData == null || string.IsNullOrEmpty(item.groupName))
            {
                return null;
            }

            HashSet<string> layoutNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < data.listPngData.Count; i++)
            {
                PicData pic = data.listPngData[i];
                if (pic.uiType == "Layout")
                {
                    if (!string.IsNullOrEmpty(pic.cleanName))
                    {
                        layoutNames.Add(pic.cleanName);
                    }
                    if (!string.IsNullOrEmpty(pic.pngName))
                    {
                        layoutNames.Add(pic.pngName);
                    }
                }
            }

            if (layoutNames.Count == 0)
            {
                return null;
            }

            string[] parts = item.groupName.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                string part = parts[i];
                if (layoutNames.Contains(part))
                {
                    return part;
                }
            }

            return null;
        }

        private static string GetNearestLayoutContainerName(Transform node, Transform root)
        {
            Transform current = node.parent;
            while (current != null && current != root)
            {
                if (current.GetComponent<LayoutGroup>() != null)
                {
                    return current.name;
                }
                current = current.parent;
            }
            return null;
        }
    }
}
