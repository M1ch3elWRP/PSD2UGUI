using System;
using System.Collections.Generic;
using UnityEngine;

namespace PSDImporter
{
    public static class PSDMatchGeometry
    {
        public struct NodeGeom
        {
            public Vector2 centerLocal;
            public Vector2 sizeLocal;
            public Vector2 rectMinLocal;
            public Vector2 rectMaxLocal;
            public bool hasLayoutBounds;
            public Vector2 layoutCenterLocal;
            public Vector2 layoutSizeLocal;
            public Vector2 layoutRectMinLocal;
            public Vector2 layoutRectMaxLocal;
            public int depth;
            public Vector2 anchorCenter;
            public Vector2 pivot;
            public string geometrySource;
        }

        public struct PsdGeom
        {
            public Vector2 centerLocal;
            public Vector2 sizeLocal;
            public Vector2 rectMinLocal;
            public Vector2 rectMaxLocal;
            public int depth;
            public Vector2 anchorCenter;
            public Vector2 pivot;
        }

        public static NodeGeom ExtractNodeGeom(RectTransform node, RectTransform root)
        {
            NodeGeom geom = default;
            if (node == null || root == null)
            {
                return geom;
            }

            if (!node.IsChildOf(root) && node != root)
            {
                return geom;
            }

            NodeGeom ownGeom = ExtractRectTransformGeom(node, root);
            if (TryExtractTextVisualBounds(node, root, ownGeom, out NodeGeom textGeom))
            {
                return textGeom;
            }

            if (TryExtractChildVisualBounds(node, root, ownGeom, out NodeGeom childGeom))
            {
                return childGeom;
            }

            return ownGeom;
        }

        private static NodeGeom ExtractRectTransformGeom(RectTransform node, RectTransform root)
        {
            NodeGeom geom = default;
            if (node == null || root == null)
            {
                return geom;
            }

            Vector3[] worldCorners = new Vector3[4];
            node.GetWorldCorners(worldCorners);
            Vector3 localCorner = root.InverseTransformPoint(worldCorners[0]);
            Vector2 min = new Vector2(localCorner.x, localCorner.y);
            Vector2 max = min;

            for (int i = 1; i < worldCorners.Length; i++)
            {
                localCorner = root.InverseTransformPoint(worldCorners[i]);
                Vector2 local2 = new Vector2(localCorner.x, localCorner.y);
                min = Vector2.Min(min, local2);
                max = Vector2.Max(max, local2);
            }

            geom.rectMinLocal = min;
            geom.rectMaxLocal = max;
            geom.centerLocal = (min + max) * 0.5f;
            geom.sizeLocal = max - min;
            geom.depth = GetNodeDepth(node, root);
            geom.anchorCenter = (node.anchorMin + node.anchorMax) * 0.5f;
            geom.pivot = node.pivot;
            geom.geometrySource = "rectTransform";
            return geom;
        }

        private static bool TryExtractTextVisualBounds(RectTransform node, RectTransform root, NodeGeom layoutGeom, out NodeGeom geom)
        {
            geom = default;
            if (node == null || root == null)
            {
                return false;
            }

            UILabel text = node.GetComponent<UILabel>();
            if (text == null || !TryGetTextLocalVisualBounds(text, out Rect localBounds))
            {
                return false;
            }

            Vector3[] localCorners =
            {
                new Vector3(localBounds.xMin, localBounds.yMin, 0f),
                new Vector3(localBounds.xMin, localBounds.yMax, 0f),
                new Vector3(localBounds.xMax, localBounds.yMax, 0f),
                new Vector3(localBounds.xMax, localBounds.yMin, 0f)
            };

            Vector2 min = Vector2.zero;
            Vector2 max = Vector2.zero;
            bool hasBounds = false;
            for (int i = 0; i < localCorners.Length; i++)
            {
                Vector3 rootPoint = root.InverseTransformPoint(node.TransformPoint(localCorners[i]));
                Vector2 point = new Vector2(rootPoint.x, rootPoint.y);
                if (!hasBounds)
                {
                    min = point;
                    max = point;
                    hasBounds = true;
                }
                else
                {
                    min = Vector2.Min(min, point);
                    max = Vector2.Max(max, point);
                }
            }

            Vector2 size = max - min;
            if (size.x <= 0.01f || size.y <= 0.01f)
            {
                return false;
            }

            geom.rectMinLocal = min;
            geom.rectMaxLocal = max;
            geom.centerLocal = (min + max) * 0.5f;
            geom.sizeLocal = size;
            geom.hasLayoutBounds = true;
            geom.layoutCenterLocal = layoutGeom.centerLocal;
            geom.layoutSizeLocal = layoutGeom.sizeLocal;
            geom.layoutRectMinLocal = layoutGeom.rectMinLocal;
            geom.layoutRectMaxLocal = layoutGeom.rectMaxLocal;
            geom.depth = layoutGeom.depth;
            geom.anchorCenter = layoutGeom.anchorCenter;
            geom.pivot = layoutGeom.pivot;
            geom.geometrySource = "textVisualBounds";
            return true;
        }

        public static bool TryGetTextLocalVisualBounds(UILabel text, out Rect bounds)
        {
            bounds = default;
            if (text == null ||
                !text.enabled ||
                text.cachedTransform == null ||
                text.trueTypeFont == null && text.bitmapFont == null ||
                string.IsNullOrEmpty(text.text))
            {
                return false;
            }

            Vector3 widgetSize = text.cachedTransform.localScale;
            Rect layoutRect = new Rect(-widgetSize.x * 0.5f, -widgetSize.y * 0.5f, widgetSize.x, widgetSize.y);
            if (layoutRect.width <= 0.01f || layoutRect.height <= 0.01f)
            {
                return false;
            }

            try
            {
                text.ProcessText();
                Vector2 printed = text.printedSize;
                if (printed.x <= 0.01f || printed.y <= 0.01f)
                {
                    return false;
                }

                Vector3 offset = text.cachedTransform.localPosition;
                Vector2 min = new Vector2(offset.x - printed.x * 0.5f, offset.y - printed.y * 0.5f);
                Vector2 max = new Vector2(offset.x + printed.x * 0.5f, offset.y + printed.y * 0.5f);

                if (max.x - min.x <= 0.01f || max.y - min.y <= 0.01f)
                {
                    return false;
                }

                bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryExtractChildVisualBounds(RectTransform node, RectTransform root, NodeGeom ownGeom, out NodeGeom geom)
        {
            geom = default;
            if (node == null || root == null || node.childCount == 0)
            {
                return false;
            }

            RectTransform[] children = node.GetComponentsInChildren<RectTransform>(true);
            Vector2 min = Vector2.zero;
            Vector2 max = Vector2.zero;
            bool hasBounds = false;

            for (int i = 0; i < children.Length; i++)
            {
                RectTransform child = children[i];
                if (child == null || child == node)
                {
                    continue;
                }

                if (!IsVisualChild(child))
                {
                    continue;
                }

                NodeGeom childGeom = ExtractRectTransformGeom(child, root);
                if (TryExtractTextVisualBounds(child, root, childGeom, out NodeGeom textChildGeom))
                {
                    childGeom = textChildGeom;
                }

                if (!hasBounds)
                {
                    min = childGeom.rectMinLocal;
                    max = childGeom.rectMaxLocal;
                    hasBounds = true;
                }
                else
                {
                    min = Vector2.Min(min, childGeom.rectMinLocal);
                    max = Vector2.Max(max, childGeom.rectMaxLocal);
                }
            }

            if (!hasBounds)
            {
                return false;
            }

            Vector2 childSize = max - min;
            if (!ShouldPreferChildBounds(node, ownGeom.sizeLocal, childSize))
            {
                return false;
            }

            geom.rectMinLocal = min;
            geom.rectMaxLocal = max;
            geom.centerLocal = (min + max) * 0.5f;
            geom.sizeLocal = childSize;
            geom.depth = GetNodeDepth(node, root);
            geom.anchorCenter = (node.anchorMin + node.anchorMax) * 0.5f;
            geom.pivot = node.pivot;
            geom.geometrySource = "childVisualBounds";
            return true;
        }

        private static bool ShouldPreferChildBounds(RectTransform node, Vector2 ownSize, Vector2 childSize)
        {
            if (childSize.x <= 0.01f || childSize.y <= 0.01f)
            {
                return false;
            }

            bool hasLayoutGroup = node.GetComponent<UITable>() != null || node.GetComponent<UIGrid>() != null;
            bool hasGraphic = node.GetComponent<UIWidget>() != null;
            float ownArea = Mathf.Max(0.01f, ownSize.x * ownSize.y);
            float childArea = childSize.x * childSize.y;
            bool ownClearlySmaller =
                ownSize.x < childSize.x * 0.7f ||
                ownSize.y < childSize.y * 0.7f ||
                ownArea < childArea * 0.55f;
            bool structuralContainer = !hasGraphic && node.childCount > 0;

            return hasLayoutGroup || ownClearlySmaller || (structuralContainer && ownArea < childArea * 0.9f);
        }

        private static bool IsVisualChild(RectTransform child)
        {
            if (child == null)
            {
                return false;
            }

            UIWidget graphic = child.GetComponent<UIWidget>();
            if (graphic != null && graphic.enabled)
            {
                return true;
            }

            return child.GetComponent<UIWidget>() != null || child.GetComponent<UITable>() != null || child.GetComponent<UIGrid>() != null;
        }

        public static Rect ToGuiRect(NodeGeom geom, Vector2 canvasOffset, float zoom)
        {
            return new Rect(
                canvasOffset.x + geom.rectMinLocal.x * zoom,
                canvasOffset.y - geom.rectMaxLocal.y * zoom,
                geom.sizeLocal.x * zoom,
                geom.sizeLocal.y * zoom);
        }

        public static void RebuildLayoutForGeometry(RectTransform root)
        {
            if (root == null)
            {
                return;
            }

            // NGUI 不使用 Canvas；通知所有 UIPanel 重排并标记父级变更，让 UITable/UIGrid 立刻 Reposition
            var panels = root.GetComponentsInChildren<UIPanel>(true);
            for (int i = 0; i < panels.Length; i++)
            {
                if (panels[i] != null) panels[i].SetDirty();
            }
            NGUITools.MarkParentChanged();

            RectTransform[] rects = root.GetComponentsInChildren<RectTransform>(true);
            Array.Sort(rects, (a, b) => GetNodeDepth(b, root).CompareTo(GetNodeDepth(a, root)));
            for (int i = 0; i < rects.Length; i++)
            {
                if (rects[i] != null)
                {
                    var table = rects[i].GetComponent<UITable>();
                    if (table != null) table.repositionNow = true;

                    var grid = rects[i].GetComponent<UIGrid>();
                    if (grid != null) grid.repositionNow = true;
                }
            }
            NGUITools.MarkParentChanged();
        }

        public static PsdGeom BuildPsdGeom(PicData item, int psdWidth, int psdHeight)
        {
            PsdGeom geom = default;
            if (item.Equals(null))
            {
                return geom;
            }

            Vector2 center = new Vector2(item.x - psdWidth * 0.5f, item.y - psdHeight * 0.5f);
            Vector2 size = new Vector2(item.width, item.height);

            geom.centerLocal = center;
            geom.sizeLocal = size;
            geom.rectMinLocal = center - size * 0.5f;
            geom.rectMaxLocal = center + size * 0.5f;
            geom.depth = GetPsdDepth(item.groupName);
            geom.anchorCenter = new Vector2(0.5f, 0.5f);
            geom.pivot = new Vector2(0.5f, 0.5f);
            return geom;
        }

        public static int GetPsdDepth(string groupName)
        {
            if (string.IsNullOrEmpty(groupName)) return 0;
            string trimmed = groupName.Trim('/');
            if (string.IsNullOrEmpty(trimmed)) return 0;
            return trimmed.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        public static int GetNodeDepth(Transform node, Transform root)
        {
            int depth = 0;
            Transform current = node;
            while (current != null && current != root)
            {
                depth++;
                current = current.parent;
            }
            return depth;
        }
    }
}
