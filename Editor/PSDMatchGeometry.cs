using System;
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
            public int depth;
            public Vector2 anchorCenter;
            public Vector2 pivot;
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
            return geom;
        }

        public static PsdGeom BuildPsdGeom(PicData item, int psdWidth, int psdHeight)
        {
            PsdGeom geom = default;
            if (item == null)
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
