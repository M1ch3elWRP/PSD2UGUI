using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PSDImporter
{
    internal static class PSDScrollRectUtility
    {
        internal const string UiType = "ScrollRect";

        internal static bool IsScrollRectRoot(PicData item)
        {
            return string.Equals(item.uiType, UiType, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsScrollRectNode(Transform node)
        {
            return node != null && node.GetComponent<UIScrollView>() != null;
        }

        internal static bool IsScrollContentAlias(PicData item)
        {
            return item.isScrollContentAlias && item.scrollRectRootId > 0;
        }

        internal static bool TryGetContentAlias(PSDData psdData, int scrollRootId, out PicData alias)
        {
            alias = default;
            if (psdData == null || psdData.listPngData == null || scrollRootId <= 0)
            {
                return false;
            }

            for (int i = 0; i < psdData.listPngData.Count; i++)
            {
                PicData item = psdData.listPngData[i];
                if (item.isScrollContentAlias && item.scrollRectRootId == scrollRootId)
                {
                    alias = item;
                    return true;
                }
            }

            return false;
        }

        internal static bool TryGetContentAliasRootId(PSDData psdData, int aliasId, out int scrollRootId)
        {
            scrollRootId = 0;
            if (psdData == null || psdData.listPngData == null || aliasId <= 0)
            {
                return false;
            }

            for (int i = 0; i < psdData.listPngData.Count; i++)
            {
                PicData item = psdData.listPngData[i];
                if (item.id == aliasId && item.isScrollContentAlias && item.scrollRectRootId > 0)
                {
                    scrollRootId = item.scrollRectRootId;
                    return true;
                }
            }

            return false;
        }

        internal static RectTransform EnsureScrollRectStructure(GameObject rootGo, PicData scrollItem, PSDData psdData, PSDImportConfig config)
        {
            if (rootGo == null)
            {
                return null;
            }

            RectTransform rootRt = rootGo.GetComponent<RectTransform>();
            if (rootRt == null)
            {
                rootRt = rootGo.AddComponent<RectTransform>();
            }

            string layoutType = ResolveContentLayoutType(scrollItem, psdData);
            bool horizontal = string.Equals(layoutType, "Horizontal", StringComparison.OrdinalIgnoreCase);
            bool vertical = !horizontal;

            UIScrollView scrollView = rootGo.GetComponent<UIScrollView>();
            bool createdScrollRect = scrollView == null;

            if (createdScrollRect)
            {
                scrollView = Undo.AddComponent<UIScrollView>(rootGo);
                scrollView.movementType = UIScrollView.Movement.Elastic;
                scrollView.dragEffect = UIScrollView.DragEffect.MomentumAndSpring;
                scrollView.scrollWheelFactor = 1f;
                scrollView.momentumAmount = 35f;
            }
            scrollView.canBeDragged = true;
            scrollView.disableDrag = false;

            UIPanel rootPanel = rootGo.GetComponent<UIPanel>();
            if (rootPanel == null)
            {
                rootPanel = Undo.AddComponent<UIPanel>(rootGo);
            }
            rootPanel.clipping = UIDrawCall.Clipping.SoftClip;
            Vector2 rootSize = rootRt.rect.size;
            rootPanel.clipRange = new Vector4(0f, 0f, Mathf.Max(1f, rootSize.x), Mathf.Max(1f, rootSize.y));
            rootPanel.clipSoftness = Vector2.zero;
            scrollView.panel = rootPanel;

            RectTransform viewport = ResolveViewport(rootRt, scrollView);
            if (viewport == null && createdScrollRect)
            {
                viewport = CreateChildRect("Viewport", rootRt);
                StretchToParent(viewport);
                UISprite viewportImage = viewport.GetComponent<UISprite>();
                if (viewportImage == null)
                {
                    viewportImage = viewport.gameObject.AddComponent<UISprite>();
                }
                viewportImage.atlas = PSDCreateor.GetCachedAtlas();
                viewportImage.spriteName = "White";
                viewportImage.color = new Color(1f, 1f, 1f, 0f);
                UIPanel viewportPanel = viewport.GetComponent<UIPanel>();
                if (viewportPanel == null)
                {
                    viewportPanel = viewport.gameObject.AddComponent<UIPanel>();
                }
                viewportPanel.clipping = UIDrawCall.Clipping.SoftClip;
                Vector2 vpSize = viewport.rect.size;
                viewportPanel.clipRange = new Vector4(0f, 0f, Mathf.Max(1f, vpSize.x), Mathf.Max(1f, vpSize.y));
                viewportPanel.clipSoftness = Vector2.zero;
            }

            RectTransform content = ResolveContent(rootRt, scrollView);
            if (content == null && createdScrollRect)
            {
                content = CreateChildRect("Content", viewport != null ? viewport : rootRt);
            }

            if (content == null)
            {
                return null;
            }

            ConfigureContentRect(content, layoutType);

            scrollView.content = content;

            ApplyContentLayout(content, scrollItem, psdData, config, layoutType);
            return content;
        }

        internal static RectTransform GetContentTransform(Transform scrollRoot)
        {
            if (scrollRoot == null)
            {
                return null;
            }

            RectTransform rootRt = scrollRoot as RectTransform;
            if (rootRt == null)
            {
                return null;
            }

            return ResolveContent(rootRt, scrollRoot.GetComponent<UIScrollView>());
        }

        internal static void RegisterHierarchyNode(PicData item, Transform node, PSDData psdData, Dictionary<int, Transform> psdIdToNode)
        {
            if (node == null || psdIdToNode == null || item.id == 0)
            {
                return;
            }

            if (!IsScrollRectRoot(item))
            {
                psdIdToNode[item.id] = node;
                return;
            }

            Transform content = GetContentTransform(node);
            Transform target = content != null ? content : node;
            psdIdToNode[item.id] = target;

            if (TryGetContentAlias(psdData, item.id, out PicData alias) && alias.id != 0)
            {
                psdIdToNode[alias.id] = target;
            }
        }

        internal static Transform ResolveParentForItem(PicData item, PSDData psdData, Dictionary<int, Transform> psdIdToNode)
        {
            if (psdIdToNode == null || item.parentNodeId <= 0)
            {
                return null;
            }

            if (psdIdToNode.TryGetValue(item.parentNodeId, out Transform directParent) && directParent != null)
            {
                return directParent;
            }

            if (!TryGetScrollRootAncestorId(item, psdData, out int scrollRootId))
            {
                return null;
            }

            if (psdIdToNode.TryGetValue(scrollRootId, out Transform contentParent) && contentParent != null)
            {
                return contentParent;
            }

            if (TryGetContentAlias(psdData, scrollRootId, out PicData alias) &&
                alias.id != 0 &&
                psdIdToNode.TryGetValue(alias.id, out Transform aliasParent) &&
                aliasParent != null)
            {
                return aliasParent;
            }

            return null;
        }

        internal static bool IsInsideScrollRect(PicData item, PSDData psdData)
        {
            return TryGetScrollRootAncestorId(item, psdData, out _);
        }

        internal static bool TryGetScrollRootAncestorId(PicData item, PSDData psdData, out int scrollRootId)
        {
            scrollRootId = 0;
            if (psdData == null || psdData.listPngData == null || item.parentNodeId <= 0)
            {
                return false;
            }

            Dictionary<int, PicData> byId = BuildItemMap(psdData);
            Dictionary<int, int> parentById = BuildParentMap(psdData);
            int cursor = item.parentNodeId;
            int guard = 0;
            while (cursor > 0 && guard++ < 2048)
            {
                if (byId.TryGetValue(cursor, out PicData current) && IsScrollRectRoot(current))
                {
                    scrollRootId = cursor;
                    return true;
                }

                if (!parentById.TryGetValue(cursor, out cursor))
                {
                    break;
                }
            }

            return false;
        }

        internal static bool TryGetContentLayoutData(PicData scrollItem, PSDData psdData, out PicData layoutData)
        {
            if (TryGetContentAlias(psdData, scrollItem.id, out layoutData))
            {
                return true;
            }

            layoutData = scrollItem;
            layoutData.layoutType = ResolveContentLayoutType(scrollItem, psdData);
            return !string.IsNullOrEmpty(layoutData.layoutType) &&
                   !string.Equals(layoutData.layoutType, "None", StringComparison.OrdinalIgnoreCase);
        }

        internal static List<PicData> GetContentLayoutChildren(PicData scrollItem, PSDData psdData)
        {
            if (!TryGetContentLayoutData(scrollItem, psdData, out PicData layoutData))
            {
                return new List<PicData>();
            }

            return GetDirectVisualChildren(layoutData, psdData);
        }

        internal static string ResolveContentLayoutType(PicData scrollItem, PSDData psdData)
        {
            if (!string.IsNullOrEmpty(scrollItem.layoutType) &&
                !string.Equals(scrollItem.layoutType, "None", StringComparison.OrdinalIgnoreCase))
            {
                return scrollItem.layoutType;
            }

            if (TryGetContentAlias(psdData, scrollItem.id, out PicData alias) &&
                !string.IsNullOrEmpty(alias.layoutType) &&
                !string.Equals(alias.layoutType, "None", StringComparison.OrdinalIgnoreCase))
            {
                return alias.layoutType;
            }

            List<PicData> children = GetDirectVisualChildren(scrollItem, psdData);
            if (children.Count <= 1)
            {
                return "Vertical";
            }

            float minX = children.Min(c => c.x);
            float maxX = children.Max(c => c.x);
            float minY = children.Min(c => c.y);
            float maxY = children.Max(c => c.y);
            return (maxX - minX) > (maxY - minY) ? "Horizontal" : "Vertical";
        }

        internal static List<PicData> GetDirectVisualChildren(PicData parentItem, PSDData psdData)
        {
            List<PicData> children = new List<PicData>();
            if (psdData == null || psdData.listPngData == null || parentItem.id == 0)
            {
                return children;
            }

            for (int i = 0; i < psdData.listPngData.Count; i++)
            {
                PicData child = psdData.listPngData[i];
                if (child.id == parentItem.id || child.excludeFromRestore || !child.hasParent || child.parentNodeId != parentItem.id)
                {
                    continue;
                }

                if (IsLayoutChildCandidate(child))
                {
                    children.Add(child);
                }
            }

            return children;
        }

        private static RectTransform ResolveViewport(RectTransform root, UIScrollView scrollView)
        {
            if (scrollView != null && scrollView.content != null && scrollView.content.parent != null)
            {
                RectTransform vp = scrollView.content.parent as RectTransform;
                if (vp != null) return vp;
            }

            return FindChildRect(root, "Viewport", recursive: false) ?? FindChildRect(root, "Viewport", recursive: true);
        }

        private static RectTransform ResolveContent(RectTransform root, UIScrollView scrollView)
        {
            if (scrollView != null && scrollView.content != null)
            {
                return scrollView.content as RectTransform;
            }

            RectTransform viewport = ResolveViewport(root, scrollView);
            if (viewport != null)
            {
                RectTransform contentUnderViewport = FindChildRect(viewport, "Content", recursive: false) ?? FindChildRect(viewport, "Content", recursive: true);
                if (contentUnderViewport != null)
                {
                    return contentUnderViewport;
                }
            }

            return FindChildRect(root, "Content", recursive: false) ?? FindChildRect(root, "Content", recursive: true);
        }

        private static RectTransform FindChildRect(Transform root, string name, bool recursive)
        {
            if (root == null)
            {
                return null;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child == null)
                {
                    continue;
                }

                if (string.Equals(child.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return child as RectTransform;
                }

                if (recursive)
                {
                    RectTransform nested = FindChildRect(child, name, true);
                    if (nested != null)
                    {
                        return nested;
                    }
                }
            }

            return null;
        }

        private static RectTransform CreateChildRect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = parent != null ? parent.gameObject.layer : LayerMask.NameToLayer("UI");
            go.transform.SetParent(parent, false);
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
            return go.GetComponent<RectTransform>();
        }

        private static void StretchToParent(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = Vector2.zero;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void ConfigureContentRect(RectTransform content, string layoutType)
        {
            if (content == null)
            {
                return;
            }

            bool horizontal = string.Equals(layoutType, "Horizontal", StringComparison.OrdinalIgnoreCase);
            if (horizontal)
            {
                content.anchorMin = new Vector2(0f, 0f);
                content.anchorMax = new Vector2(0f, 1f);
                content.pivot = new Vector2(0f, 0.5f);
            }
            else
            {
                content.anchorMin = new Vector2(0f, 1f);
                content.anchorMax = new Vector2(1f, 1f);
                content.pivot = new Vector2(0.5f, 1f);
            }

            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;
        }

        private static void ApplyContentLayout(RectTransform content, PicData scrollItem, PSDData psdData, PSDImportConfig config, string fallbackLayoutType)
        {
            if (content == null)
            {
                return;
            }

            PicData layoutData;
            if (!TryGetContentLayoutData(scrollItem, psdData, out layoutData))
            {
                layoutData = scrollItem;
                layoutData.layoutType = fallbackLayoutType;
            }

            if (string.IsNullOrEmpty(layoutData.layoutType) ||
                string.Equals(layoutData.layoutType, "None", StringComparison.OrdinalIgnoreCase))
            {
                layoutData.layoutType = fallbackLayoutType;
            }

            List<PicData> children = GetDirectVisualChildren(layoutData, psdData);
            if (children.Count == 0 && layoutData.id != scrollItem.id)
            {
                children = GetDirectVisualChildren(scrollItem, psdData);
            }

            if (children.Count > 0)
            {
                PSDLayoutTool.ApplyLayoutGroup(content.gameObject, layoutData, children, config);
                ApplyTemplateChildSize(content, children);
            }

            // NGUI 没有 ContentSizeFitter：用 NGUIMath.CalculateRelativeWidgetBounds 算子节点 bounds 后 SetSize
            Bounds b = NGUIMath.CalculateRelativeWidgetBounds(content.transform);
            if (b.size.sqrMagnitude > 0.001f)
            {
                content.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, b.size.x);
                content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, b.size.y);
            }

            var table = content.GetComponent<UITable>();
            if (table != null) table.repositionNow = true;
            var grid = content.GetComponent<UIGrid>();
            if (grid != null) grid.repositionNow = true;
            NGUITools.MarkParentChanged();

            // 通知 scrollView 重新约束
            var scrollView = content.GetComponentInParent<UIScrollView>();
            if (scrollView != null && scrollView.panel != null)
            {
                scrollView.panel.SetDirty();
                scrollView.ConstrainToBounds(true);
            }
        }

        private static void ApplyTemplateChildSize(RectTransform content, List<PicData> children)
        {
            PicData template = children.FirstOrDefault(c => string.Equals(c.uiType, "Item", StringComparison.OrdinalIgnoreCase));
            if (template.width <= 0f && children.Count > 0)
            {
                template = children[0];
            }

            if (template.width <= 0f || template.height <= 0f)
            {
                return;
            }

            UIGrid grid = content.GetComponent<UIGrid>();
            if (grid != null)
            {
                grid.cellWidth = template.width;
                grid.cellHeight = template.height;
            }

            foreach (Transform child in content)
            {
                if (child == null || !child.gameObject.activeSelf)
                {
                    continue;
                }

                RectTransform childRt = child as RectTransform;
                if (childRt == null)
                {
                    continue;
                }

                Undo.RecordObject(childRt, "Sync Scroll Item Size");
                childRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, template.width);
                childRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, template.height);
            }
        }

        private static bool IsLayoutChildCandidate(PicData child)
        {
            return string.Equals(child.uiType, "Item", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(child.uiType, "Layout", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(child.uiType, "Image", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(child.uiType, "Button", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(child.uiType, "Text", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<int, PicData> BuildItemMap(PSDData psdData)
        {
            Dictionary<int, PicData> map = new Dictionary<int, PicData>();
            if (psdData == null || psdData.listPngData == null)
            {
                return map;
            }

            for (int i = 0; i < psdData.listPngData.Count; i++)
            {
                PicData item = psdData.listPngData[i];
                if (item.id != 0 && !map.ContainsKey(item.id))
                {
                    map.Add(item.id, item);
                }
            }

            return map;
        }

        private static Dictionary<int, int> BuildParentMap(PSDData psdData)
        {
            Dictionary<int, int> map = new Dictionary<int, int>();
            if (psdData == null)
            {
                return map;
            }

            if (psdData.skeleton != null)
            {
                for (int i = 0; i < psdData.skeleton.Count; i++)
                {
                    PsdSkeletonNode node = psdData.skeleton[i];
                    if (node != null && node.nodeId != 0)
                    {
                        map[node.nodeId] = node.parentNodeId;
                    }
                }
            }

            if (psdData.listPngData != null)
            {
                for (int i = 0; i < psdData.listPngData.Count; i++)
                {
                    PicData item = psdData.listPngData[i];
                    if (item.id != 0)
                    {
                        map[item.id] = item.parentNodeId;
                    }
                }
            }

            return map;
        }
    }
}
