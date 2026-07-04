using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI; // 引用UI命名空间

namespace PSDImporter
{
    public class PSDCreateor
    {
        public static event System.Action<GameObject, string> OnNodeCreated;
        public static event System.Action<GameObject, string> OnNodeSynced;
        public static event System.Action<GameObject, string> OnPicMissing;

        private static HashSet<Transform> matchedNodes = new HashSet<Transform>();

        private static void LogImageReuseSummary(PSDImportConfig config)
        {
            if (config != null && !config.showDetailedLog) return;
            Debug.Log("[PSD ImageReuse] " + PSDImageReuseLogStore.BuildSummary());
        }

        // =========================================================================
        // 生成模式 (Generate)
        // =========================================================================
        public static RectTransform CreateUGUI_GenerateMode(PSDData psdData, PSDImportConfig config)
        {
            AssetDatabase.Refresh();
            PSDAssetDeduper.EnsureScope(psdData.psdAssetsFolder);
            PSDImageReuseLogStore.Reset();
            var rootRectTrans = CreateCanvasRoot(new DirectoryInfo(psdData.psdAssetsFolder).Name);

            rootRectTrans.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, psdData.width);
            rootRectTrans.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, psdData.height);
            rootRectTrans.anchoredPosition = Vector2.zero;
            rootRectTrans.localScale = Vector3.one;

            if (psdData.HasSkeleton)
            {
                CreateUGUI_GenerateModeWithSkeleton(psdData, config, rootRectTrans);
                LogImageReuseSummary(config);
                Debug.Log("<color=cyan>[PSD Create] 生成完毕（Skeleton优先）。</color>");
                return rootRectTrans;
            }

            foreach (var item in psdData.listPngData)
            {
                if (item.excludeFromRestore)
                {
                    continue;
                }

                RectTransform tranParent = BuildHierarchy(rootRectTrans, item.groupName);
                GameObject createdGo = CreateNodeObject(item, tranParent, config);

                // 刷新数据
                ApplyCreateModeNode(createdGo, item, psdData, config);

                OnNodeCreated?.Invoke(createdGo, item.pngName);
            }
            Debug.Log("<color=cyan>[PSD Create] 生成完毕。</color>");
            return rootRectTrans;
        }

        private static void CreateUGUI_GenerateModeWithSkeleton(PSDData psdData, PSDImportConfig config, RectTransform rootRectTrans)
        {
            var nodeGoMap = new Dictionary<int, GameObject>();
            var assetByNodeId = new Dictionary<int, PicData>();
            foreach (var pic in psdData.listPngData)
            {
                if (pic.excludeFromRestore)
                {
                    continue;
                }

                if (pic.id != 0 && !assetByNodeId.ContainsKey(pic.id))
                {
                    assetByNodeId[pic.id] = pic;
                }
            }
            var orderedNodes = new List<PsdSkeletonNode>(psdData.skeleton);
            orderedNodes.Sort((a, b) =>
            {
                int d = a.depth.CompareTo(b.depth);
                if (d != 0) return d;
                // 降序：siblingIndex 大的（PS中靠下/底层）后创建 → Hierarchy 靠下 → 后渲染 → 显示在上面 ✅
                // siblingIndex 小的（PS中靠上/顶层）先创建 → Hierarchy 靠上 → 先渲染 → 被覆盖在底下 ✅
                return b.siblingIndex.CompareTo(a.siblingIndex);
            });

            // =========================================================================
            // 空节点裁剪：标记"有意义"节点 + 保留祖先链，跳过纯空结构节点
            // 有意义 = 有视觉输出 / 是布局容器(@H/@V/@G) / 是功能容器(@Btn/@Item)
            // 同时保留有意义节点的所有祖先（否则层级链断裂）
            // =========================================================================
            var keepNode = new HashSet<int>(); // nodeId → 是否保留为 GameObject
            var nodeById = new Dictionary<int, PsdSkeletonNode>();
            foreach (var n in orderedNodes) nodeById[n.nodeId] = n;

            // Pass 1: 标记有意义的叶子/容器节点
            foreach (var node in orderedNodes)
            {
                bool meaningful = false;
                // 条件1(主要): 有对应的导出资源(PNG/Text)——这是最靠谱的"有用"标志
                if (!string.IsNullOrEmpty(node.exportAssetRef)) meaningful = true;
                // 条件2: 是布局容器(@H/@V/@G)
                else if (node.layoutHint == "Horizontal" || node.layoutHint == "Vertical" || node.layoutHint == "Grid")
                    meaningful = true;
                // 条件3: 是功能容器（Button / Item）
                else if (node.uiTypeHint == "Button" || node.uiTypeHint == "Item" || node.uiTypeHint == "ScrollRect")
                    meaningful = true;

                if (meaningful) keepNode.Add(node.nodeId);
            }

            // Pass 2: 向上传播 —— 有意义节点的所有祖先也必须保留（保持层级链路）
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var node in orderedNodes)
                {
                    if (!keepNode.Contains(node.nodeId)) continue;      // 此节点已确定不保留
                    if (!node.hasParent || node.parentNodeId <= 0) continue; // 无父节点
                    if (keepNode.Contains(node.parentNodeId)) continue;   // 父节点已在保留列表

                    keepNode.Add(node.parentNodeId);                     // 祖先也必须保留
                    changed = true;
                }
            }

            // 1) 按骨架还原层级（仅保留有意义节点 + 其祖先）
            int prunedCount = 0;
            foreach (var node in orderedNodes)
            {
                if (!keepNode.Contains(node.nodeId))
                {
                    prunedCount++;
                    continue; // ← 跳过纯空结构节点
                }

                if (PSDScrollRectUtility.TryGetContentAliasRootId(psdData, node.nodeId, out int scrollRootId) &&
                    nodeGoMap.TryGetValue(scrollRootId, out GameObject scrollRootGo))
                {
                    PicData scrollRootItem = assetByNodeId.TryGetValue(scrollRootId, out PicData foundRootItem) ? foundRootItem : default;
                    RectTransform content = PSDScrollRectUtility.EnsureScrollRectStructure(scrollRootGo, scrollRootItem, psdData, config);
                    if (content != null)
                    {
                        nodeGoMap[node.nodeId] = content.gameObject;
                    }
                    continue;
                }

                Transform parent = rootRectTrans;
                if (node.hasParent && nodeGoMap.TryGetValue(node.parentNodeId, out var parentGo))
                {
                    parent = ResolveCreateParentForSkeletonNode(parentGo.transform, node.parentNodeId, psdData, config);
                }

                string nodeName = !string.IsNullOrEmpty(node.name) ? node.name : node.rawLayerName;
                if (string.IsNullOrEmpty(nodeName)) nodeName = $"node_{node.nodeId}";
                assetByNodeId.TryGetValue(node.nodeId, out var assetItem);
                GameObject nodeGo = CreateGo<RectTransform>(nodeName, parent, "UI").gameObject;

                var nodeRt = nodeGo.GetComponent<RectTransform>() ?? nodeGo.AddComponent<RectTransform>();
                nodeRt.localScale = Vector3.one;
                nodeRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, node.width);
                nodeRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, node.height);
                nodeRt.anchoredPosition = PsdTopLeftToAnchored(node.x, node.y, node.width, node.height, psdData.width, psdData.height);

                if (assetItem.id != 0 && PSDScrollRectUtility.IsScrollRectRoot(assetItem))
                {
                    PSDScrollRectUtility.EnsureScrollRectStructure(nodeGo, assetItem, psdData, config);
                }

                nodeGoMap[node.nodeId] = nodeGo;
            }
            if (prunedCount > 0)
                Debug.Log($"<color=yellow>[PSD Create] Skeleton 已裁剪 {prunedCount} 个空节点（仅保留有视觉输出/Layout/Btn/Item 及其祖先）。</color>");

            // 2) 再按 assets 刷新视觉/组件到对应节点
            foreach (var item in psdData.listPngData)
            {
                if (item.excludeFromRestore)
                {
                    continue;
                }

                GameObject targetGo = null;
                if (item.id != 0 && nodeGoMap.TryGetValue(item.id, out var found))
                {
                    targetGo = found;
                }
                else
                {
                    // 兜底：维持旧逻辑
                    RectTransform tranParent = BuildHierarchy(rootRectTrans, item.groupName);
                    targetGo = CreateNodeObject(item, tranParent, config);
                }

                ApplyCreateModeNode(targetGo, item, psdData, config);
                OnNodeCreated?.Invoke(targetGo, item.pngName);
            }
        }

        // =========================================================================
        // 同步模式 (Sync) - 这里的入口主要用于旧菜单，窗口模式主要调用 RefreshNode
        // =========================================================================
        public static void CreateUGUI_SyncMode(PSDData psdData, Transform root, PSDImportConfig config)
        {
            AssetDatabase.Refresh();
            PSDAssetDeduper.EnsureScope(psdData.psdAssetsFolder);
            PSDImageReuseLogStore.Reset();
            matchedNodes.Clear();
            int count = 0;

            // 注意：这里没有传入 BindingData，所以只跑基础算法。
            // 建议使用 VisualBindingWindow 来进行带 ID 记忆的同步。
            if (config.showDetailedLog) Debug.Log($"<b>[开始同步]</b> 目标根节点: {root.name}");

            foreach (var item in psdData.listPngData)
            {
                // 使用策略类查找
                var result = PSDMatchingStrategy.FindBestMatch(item, root, psdData.width, psdData.height, config, null, matchedNodes);

                if (result == null) continue;
                Transform target = result.target;
                matchedNodes.Add(target);

                count++;
                RefreshNode(target.gameObject, item, psdData, true, config);

                OnNodeSynced?.Invoke(target.gameObject, item.pngName);
            }
            LogImageReuseSummary(config);
            Debug.Log($"<color=green>[同步完成] 成功匹配: {count} / {psdData.listPngData.Count}</color>");
        }

        // =========================================================================
        // 核心能力 (RefreshNode) - 供窗口调用
        // =========================================================================
        public static void RefreshNode(GameObject go, PicData item, PSDData psdData, bool updatePosition, PSDImportConfig config = null)
        {
            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt == null) rt = go.AddComponent<RectTransform>();
            if (config != null && config.dedupeSprites) PSDAssetDeduper.EnsureScope(psdData.psdAssetsFolder);

            // 1. 设置尺寸 & 位置 (保持原样)
            Undo.RecordObject(rt, "Sync Transform");
            Vector2 previousSize = new Vector2(rt.rect.width, rt.rect.height);
            bool controlledByLayout = updatePosition && IsControlledByPsdLayoutParent(go.transform, item, psdData);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, item.width);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, item.height);

            if (updatePosition)
            {
                if (!controlledByLayout)
                {
                    Transform canvasRoot = GetRootCanvasTransform(go.transform);
                    if (canvasRoot != null)
                    {
                        if (IsChildOfLayoutItemContainer(go.transform) &&
                            TryGetParentPsdData(go.transform.parent, item, psdData, out var parentItem))
                        {
                            ApplyPsdPositionLocal(go.transform, item, parentItem);
                        }
                        else
                        {
                            ApplyPsdPosition(go.transform, canvasRoot, item, psdData);
                        }
                    }
                }
                else
                {
                    TryApplyPsdParentSkeletonRect(go.transform, item, psdData);
                }
            }

            // 2. 组件挂载 (Case Item 修改)
            switch (item.uiType)
            {
                case "Text":
                    txt = EnsureComponent<Text>(go);
                    SetupText(txt, item, config);
                    ApplyTextLayoutAndPosition(txt, item, psdData, updatePosition, controlledByLayout, previousSize, config);

                    // 描边：UGUI 原生 Outline
                    if (item.hasStroke)
                    {
                        SetupTextOutline(go, item);
                    }

                    // 渐变：UGUI 原生无渐变 effect，用 Shadow 兜底（取渐变底色作为投影色，垂直偏移）
                    if (item.hasGradient)
                    {
                        SetupTextGradientFallback(go, item);
                    }
                    break;

                case "Button":
                    EnsureComponent<Button>(go);
                    //if (go.GetComponent<Image>() == null)
                    //{
                    //    var img = go.AddComponent<Image>();
                    //    img.color = new Color(0, 0, 0, 0);
                    //}
                    break;

                case "Item":
                    // 这是一个 @Item 模板节点
                    // 它本身不需要挂载特殊组件，仅仅作为数据源
                    // 它的尺寸已经在上面 SetSize 里设置好了
                    break;

                case "Layout":
                    // 纯布局容器，无需 Image
                    break;

                case "ScrollRect":
                    PSDScrollRectUtility.EnsureScrollRectStructure(go, item, psdData, config);
                    break;

                case "Image":
                default:
                    if (item.layoutType == "None")
                    {
                        var img = EnsureComponent<Image>(go);
                        SetupImage(img, item, psdData.psdAssetsFolder, config);
                    }
                    break;
            }

            // 3. 处理 LayoutGroup 逻辑 (核心修改)
            if (item.layoutType != "None" && !PSDScrollRectUtility.IsScrollRectRoot(item))
            {
                // A. 收集 PSD 里属于该 Layout 的子数据 (用于计算 Padding/Spacing)
                List<PicData> layoutChildren = new List<PicData>();

                if (psdData.HasSkeleton)
                {
                    // Skeleton 模式：用 parentNodeId 层级关系查找直接子节点（精确，不依赖几何包含）
                    int layoutNodeId = item.id;
                    foreach (var skelNode in psdData.skeleton)
                    {
                        if (skelNode.parentNodeId != layoutNodeId) continue;
                        // 只认有视觉输出的子节点（@Item/@Image/@Button 或有 exportAssetRef）
                        bool isLayoutChild = !string.IsNullOrEmpty(skelNode.exportAssetRef) ||
                                             skelNode.uiTypeHint == "Item" ||
                                             skelNode.uiTypeHint == "Image" ||
                                             skelNode.uiTypeHint == "Button" ||
                                             skelNode.uiTypeHint == "Text";
                        if (!isLayoutChild) continue;

                        // 找到对应的 listPngData 项
                        if (psdData.listPngData.Any(p => p.id == skelNode.nodeId))
                        {
                            var picData = psdData.listPngData.First(p => p.id == skelNode.nodeId);
                            layoutChildren.Add(picData);
                        }
                    }
                }
                else
                {
                    // 兼容模式（无 Skeleton）：仍用几何包含判断
                    foreach (var other in psdData.listPngData)
                    {
                        if (other.id == item.id) continue;
                        if (other.uiType == "Item" || other.uiType == "Image" || other.uiType == "Button")
                        {
                            bool insideX = other.x >= (item.x - item.width / 2) && other.x <= (item.x + item.width / 2);
                            bool insideY = other.y >= (item.y - item.height / 2) && other.y <= (item.y + item.height / 2);
                            if (insideX && insideY)
                            {
                                layoutChildren.Add(other);
                            }
                        }
                    }
                }

                if (layoutChildren.Count > 0)
                {
                    bool hasItemChild = false;
                    for (int i = 0; i < layoutChildren.Count; i++)
                    {
                        if (layoutChildren[i].uiType == "Item")
                        {
                            hasItemChild = true;
                            break;
                        }
                    }

                    if (hasItemChild)
                    {
                        var onlyItems = new List<PicData>();
                        for (int i = 0; i < layoutChildren.Count; i++)
                        {
                            if (layoutChildren[i].uiType == "Item") onlyItems.Add(layoutChildren[i]);
                        }
                        layoutChildren = onlyItems;
                    }
                    // B. 应用 Layout 参数 (Padding, Spacing)
                    PSDLayoutTool.ApplyLayoutGroup(go, item, layoutChildren, config);

                    // C. 【核心逻辑】找到 @Item 模板，强行应用给所有 Unity 子节点
                    var templateItem = layoutChildren.Find(x => x.uiType == "Item");

                    // 如果没找到 @Item，但有其他子节点，也可以拿第一个作为尺寸参考(兜底)
                    if (templateItem.width == 0 && layoutChildren.Count > 0) templateItem = layoutChildren[0];

                    if (templateItem.width > 0)
                    {
                        // Grid 特殊处理：直接改 CellSize
                        var grid = go.GetComponent<GridLayoutGroup>();
                        if (grid != null)
                        {
                            grid.cellSize = new Vector2(templateItem.width, templateItem.height);
                        }

                        // 通用处理：遍历所有 Unity 子节点，强行改尺寸
                        // (Horizontal/Vertical Layout 下的子物体也需要改尺寸)
                        foreach (Transform child in go.transform)
                        {
                            if (!child.gameObject.activeSelf) continue;
                            var childRT = child.GetComponent<RectTransform>();
                            if (childRT != null)
                            {
                                Undo.RecordObject(childRT, "Sync Item Size");
                                childRT.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, templateItem.width);
                                childRT.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, templateItem.height);
                            }
                        }
                    }
                }

                // 强制刷新
                LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
            }
        }


        // =========================================================================
        // 内部辅助方法
        // =========================================================================
        // =========================================================================
        // 辅助算法：重新计算组的包围盒
        // =========================================================================
        private static PicData CalculateGroupBounds(PicData groupItem, List<PicData> children)
        {
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;

            foreach (var child in children)
            {
                float l = child.x - child.width / 2f;
                float r = child.x + child.width / 2f;
                float t = child.y - child.height / 2f;
                float b = child.y + child.height / 2f;

                if (l < minX) minX = l;
                if (r > maxX) maxX = r;
                if (t < minY) minY = t;
                if (b > maxY) maxY = b;
            }

            // 更新 Group 的数据
            groupItem.x = (minX + maxX) / 2f;
            groupItem.y = (minY + maxY) / 2f;
            groupItem.width = maxX - minX;
            groupItem.height = maxY - minY;

            return groupItem;
        }

        private static Transform GetRootCanvasTransform(Transform current)
        {
            Canvas canvas = current.GetComponentInParent<Canvas>();
            if (canvas != null) return canvas.transform;
            if (current.root != null) return current.root;
            return null;
        }

        private static GameObject CreateNodeObject(PicData item, Transform parent, PSDImportConfig config)
        {
            // Create base node; components are added in RefreshNode to honor overrides.
            return CreateGo<RectTransform>(item.cleanName, parent, "UI").gameObject;
        }

        private static void ApplyCreateModeNode(GameObject go, PicData item, PSDData psdData, PSDImportConfig config)
        {
            if (go == null)
            {
                return;
            }

            RefreshNode(go, item, psdData, true, config);
        }

        private static Transform ResolveCreateParentForSkeletonNode(Transform mappedParent, int parentNodeId, PSDData psdData, PSDImportConfig config)
        {
            if (mappedParent == null || parentNodeId <= 0 || psdData == null || psdData.listPngData == null)
            {
                return mappedParent;
            }

            for (int i = 0; i < psdData.listPngData.Count; i++)
            {
                PicData parentItem = psdData.listPngData[i];
                if (parentItem.id == parentNodeId && PSDScrollRectUtility.IsScrollRectRoot(parentItem))
                {
                    RectTransform content = PSDScrollRectUtility.EnsureScrollRectStructure(mappedParent.gameObject, parentItem, psdData, config);
                    return content != null ? content : mappedParent;
                }
            }

            return mappedParent;
        }

        private static void ApplyPsdPosition(Transform target, Transform root, PicData item, PSDData psdData)
        {
            RectTransform rt = target.GetComponent<RectTransform>();
            if (rt == null) return;
            RectTransform parentRt = target.parent as RectTransform;

            // 1. 计算 PSD 几何中心在世界坐标的位置
            float psdCenterX = item.x - psdData.width * 0.5f;
            float psdCenterY = item.y - psdData.height * 0.5f;
            Vector3 worldCenterPos = root.TransformPoint(new Vector3(psdCenterX, psdCenterY, 0));

            // 2. 计算 Pivot 偏移 (Pivot 0.5,0.5 时偏移为0)
            float pivotOffsetX = (rt.pivot.x - 0.5f) * item.width;
            float pivotOffsetY = (rt.pivot.y - 0.5f) * item.height;
            Vector3 worldPivotOffset = target.TransformVector(new Vector3(pivotOffsetX, pivotOffsetY, 0));

            // 3. 应用
            Vector3 worldPos = worldCenterPos + worldPivotOffset;
            if (parentRt == null)
            {
                target.position = worldPos;
            }
            else
            {
                Vector3 localPos = parentRt.InverseTransformPoint(worldPos);
                SetAnchoredPositionFromLocal(rt, parentRt, localPos);
            }
        }

        private static void ApplyTextLayoutAndPosition(
            Text txt,
            PicData item,
            PSDData psdData,
            bool updatePosition,
            bool controlledByLayout,
            Vector2 previousSize,
            PSDImportConfig config)
        {
            if (txt == null || psdData == null)
            {
                return;
            }

            RectTransform rt = txt.rectTransform;
            if (rt == null)
            {
                return;
            }

            Vector2 layoutSize = CalculateTextLayoutSize(txt, item, previousSize, config);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, layoutSize.x);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, layoutSize.y);
            Canvas.ForceUpdateCanvases();

            if (!updatePosition || controlledByLayout)
            {
                return;
            }

            if (!PSDMatchGeometry.TryGetTextLocalVisualBounds(txt, out Rect visualBounds))
            {
                visualBounds = rt.rect;
            }

            if (IsChildOfLayoutItemContainer(rt.transform) &&
                TryGetParentPsdData(rt.transform.parent, item, psdData, out var parentItem))
            {
                ApplyPsdTextReferencePositionLocal(rt, visualBounds, item, parentItem, txt.alignment);
                return;
            }

            Transform canvasRoot = GetRootCanvasTransform(rt.transform);
            if (canvasRoot != null)
            {
                ApplyPsdTextReferencePosition(rt, canvasRoot, visualBounds, item, psdData, txt.alignment);
            }
        }

        private static Vector2 CalculateTextLayoutSize(Text txt, PicData item, Vector2 previousSize, PSDImportConfig config)
        {
            float paddingX = config != null ? Mathf.Max(0f, config.textLayoutPaddingX) : 24f;
            float paddingY = config != null ? Mathf.Max(0f, config.textLayoutPaddingY) : 8f;
            float psdWidth = Mathf.Max(1f, item.width);
            float baseWidth = psdWidth + paddingX;
            float preferredWidth = GetTextPreferredWidth(txt, psdWidth);
            bool allowSingleLineExpansion = AllowsSingleLineTextExpansion(preferredWidth, psdWidth, config);

            float width = allowSingleLineExpansion
                ? Mathf.Max(baseWidth, preferredWidth + paddingX)
                : baseWidth;

            float preferredHeight = GetTextPreferredHeight(txt, width, item.height);
            float height = Mathf.Max(1f, item.height + paddingY, preferredHeight + paddingY);

            bool preserveLarger = config == null || config.textPreserveLargerLayoutRect;
            if (preserveLarger)
            {
                if (allowSingleLineExpansion && previousSize.x > 0.01f)
                {
                    width = Mathf.Max(width, previousSize.x);
                }

                if (previousSize.y > 0.01f)
                {
                    height = Mathf.Max(height, previousSize.y);
                }
            }

            return new Vector2(width, height);
        }

        private static bool AllowsSingleLineTextExpansion(float preferredWidth, float psdWidth, PSDImportConfig config)
        {
            if (preferredWidth <= psdWidth + 0.01f)
            {
                return true;
            }

            float ratio = config != null ? Mathf.Max(1f, config.textSingleLineExpansionMaxRatio) : 1.25f;
            float maxExtra = config != null ? Mathf.Max(0f, config.textSingleLineExpansionMaxExtraWidth) : 96f;
            float limit = Mathf.Max(psdWidth * ratio, psdWidth + maxExtra);
            return preferredWidth <= limit;
        }

        private static float GetTextPreferredWidth(Text txt, float fallbackWidth)
        {
            if (txt == null || txt.font == null || string.IsNullOrEmpty(txt.text))
            {
                return Mathf.Max(0f, fallbackWidth);
            }

            try
            {
                float pixelsPerUnit = Mathf.Max(0.01f, txt.pixelsPerUnit);
                TextGenerator generator = txt.cachedTextGeneratorForLayout;
                TextGenerationSettings widthSettings = txt.GetGenerationSettings(Vector2.zero);
                float preferredWidth = generator.GetPreferredWidth(txt.text, widthSettings) / pixelsPerUnit;
                if (!float.IsNaN(preferredWidth) && !float.IsInfinity(preferredWidth) && preferredWidth > 0f)
                {
                    return Mathf.Max(fallbackWidth, preferredWidth);
                }
            }
            catch (Exception)
            {
                if (txt.preferredWidth > 0f)
                {
                    return Mathf.Max(fallbackWidth, txt.preferredWidth);
                }
            }

            return Mathf.Max(0f, fallbackWidth);
        }

        private static float GetTextPreferredHeight(Text txt, float width, float fallbackHeight)
        {
            if (txt == null || txt.font == null || string.IsNullOrEmpty(txt.text))
            {
                return Mathf.Max(0f, fallbackHeight);
            }

            try
            {
                float pixelsPerUnit = Mathf.Max(0.01f, txt.pixelsPerUnit);
                TextGenerator generator = txt.cachedTextGeneratorForLayout;
                TextGenerationSettings heightSettings = txt.GetGenerationSettings(new Vector2(Mathf.Max(1f, width), 0f));
                float preferredHeight = generator.GetPreferredHeight(txt.text, heightSettings) / pixelsPerUnit;
                if (!float.IsNaN(preferredHeight) && !float.IsInfinity(preferredHeight) && preferredHeight > 0f)
                {
                    return Mathf.Max(fallbackHeight, preferredHeight);
                }
            }
            catch (Exception)
            {
                if (txt.preferredHeight > 0f)
                {
                    return Mathf.Max(fallbackHeight, txt.preferredHeight);
                }
            }

            return Mathf.Max(0f, fallbackHeight);
        }

        private static void ApplyPsdTextReferencePosition(RectTransform rt, Transform root, Rect visualBounds, PicData item, PSDData psdData, TextAnchor alignment)
        {
            if (rt == null || root == null || psdData == null)
            {
                return;
            }

            Vector2 localReference = GetTextReference(visualBounds, alignment);
            Vector2 psdReference = GetPsdTextReference(item, psdData.width, psdData.height, alignment);
            Vector3 desiredWorld = root.TransformPoint(new Vector3(psdReference.x, psdReference.y, 0f));
            ApplyWorldReferencePosition(rt, desiredWorld, localReference);
        }

        private static void ApplyPsdTextReferencePositionLocal(RectTransform rt, Rect visualBounds, PicData item, PicData parentItem, TextAnchor alignment)
        {
            if (rt == null || rt.parent == null)
            {
                return;
            }

            RectTransform parentRt = rt.parent as RectTransform;
            if (parentRt == null)
            {
                return;
            }

            Vector2 localReference = GetTextReference(visualBounds, alignment);
            Vector2 psdReference = GetPsdTextReferenceLocal(item, parentItem, parentRt, alignment);
            Vector3 desiredWorld = parentRt.TransformPoint(new Vector3(psdReference.x, psdReference.y, 0f));
            ApplyWorldReferencePosition(rt, desiredWorld, localReference);
        }

        private static void ApplyWorldReferencePosition(RectTransform rt, Vector3 desiredReferenceWorld, Vector2 localReference)
        {
            if (rt == null)
            {
                return;
            }

            RectTransform parentRt = rt.parent as RectTransform;
            Vector3 worldReferenceOffset = rt.TransformVector(new Vector3(localReference.x, localReference.y, 0f));
            Vector3 worldPivotPos = desiredReferenceWorld - worldReferenceOffset;
            if (parentRt == null)
            {
                rt.position = worldPivotPos;
                return;
            }

            Vector3 localPos = parentRt.InverseTransformPoint(worldPivotPos);
            SetAnchoredPositionFromLocal(rt, parentRt, localPos);
        }

        private static Vector2 GetPsdTextReference(PicData item, int psdWidth, int psdHeight, TextAnchor alignment)
        {
            Vector2 center = new Vector2(item.x - psdWidth * 0.5f, item.y - psdHeight * 0.5f);
            Rect rect = Rect.MinMaxRect(
                center.x - item.width * 0.5f,
                center.y - item.height * 0.5f,
                center.x + item.width * 0.5f,
                center.y + item.height * 0.5f);
            return GetTextReference(rect, alignment);
        }

        private static Vector2 GetPsdTextReferenceLocal(PicData item, PicData parentItem, RectTransform parentRt, TextAnchor alignment)
        {
            Vector2 parentPivotOffset = parentRt != null
                ? new Vector2(
                    (parentRt.pivot.x - 0.5f) * parentItem.width,
                    (parentRt.pivot.y - 0.5f) * parentItem.height)
                : Vector2.zero;
            Vector2 center = new Vector2(item.x - parentItem.x, item.y - parentItem.y) - parentPivotOffset;
            Rect rect = Rect.MinMaxRect(
                center.x - item.width * 0.5f,
                center.y - item.height * 0.5f,
                center.x + item.width * 0.5f,
                center.y + item.height * 0.5f);
            return GetTextReference(rect, alignment);
        }

        private static Vector2 GetTextReference(Rect rect, TextAnchor alignment)
        {
            float x = IsLeftAligned(alignment)
                ? rect.xMin
                : IsRightAligned(alignment) ? rect.xMax : rect.center.x;
            float y = IsUpperAligned(alignment)
                ? rect.yMax
                : IsLowerAligned(alignment) ? rect.yMin : rect.center.y;
            return new Vector2(x, y);
        }

        private static bool IsLeftAligned(TextAnchor alignment)
        {
            return alignment == TextAnchor.UpperLeft ||
                   alignment == TextAnchor.MiddleLeft ||
                   alignment == TextAnchor.LowerLeft;
        }

        private static bool IsRightAligned(TextAnchor alignment)
        {
            return alignment == TextAnchor.UpperRight ||
                   alignment == TextAnchor.MiddleRight ||
                   alignment == TextAnchor.LowerRight;
        }

        private static bool IsUpperAligned(TextAnchor alignment)
        {
            return alignment == TextAnchor.UpperLeft ||
                   alignment == TextAnchor.UpperCenter ||
                   alignment == TextAnchor.UpperRight;
        }

        private static bool IsLowerAligned(TextAnchor alignment)
        {
            return alignment == TextAnchor.LowerLeft ||
                   alignment == TextAnchor.LowerCenter ||
                   alignment == TextAnchor.LowerRight;
        }

        private static bool TryApplyPsdParentSkeletonRect(Transform target, PicData item, PSDData psdData)
        {
            if (target == null || target.parent == null || psdData == null || !psdData.HasSkeleton || !item.hasParent)
            {
                return false;
            }

            RectTransform parentRt = target.parent as RectTransform;
            if (parentRt == null || !HasEnabledLayoutGroup(parentRt))
            {
                return false;
            }

            if (!TryGetSkeletonNode(psdData, item.parentNodeId, out PsdSkeletonNode parentNode))
            {
                return false;
            }

            if (!IsPsdLayoutNode(parentNode))
            {
                return false;
            }

            Transform canvasRoot = GetRootCanvasTransform(parentRt);
            if (canvasRoot == null)
            {
                return false;
            }

            Undo.RecordObject(parentRt, "Sync Layout Parent From PSD Skeleton");
            ApplyPsdSkeletonNodeRect(parentRt, canvasRoot, parentNode, psdData);
            return true;
        }

        private static bool TryGetSkeletonNode(PSDData psdData, int nodeId, out PsdSkeletonNode node)
        {
            node = null;
            if (psdData == null || psdData.skeleton == null || nodeId <= 0)
            {
                return false;
            }

            for (int i = 0; i < psdData.skeleton.Count; i++)
            {
                if (psdData.skeleton[i] != null && psdData.skeleton[i].nodeId == nodeId)
                {
                    node = psdData.skeleton[i];
                    return true;
                }
            }

            return false;
        }

        private static void ApplyPsdSkeletonNodeRect(RectTransform rt, Transform root, PsdSkeletonNode node, PSDData psdData)
        {
            if (rt == null || root == null || node == null || psdData == null)
            {
                return;
            }

            float width = Mathf.Max(1f, node.width);
            float height = Mathf.Max(1f, node.height);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);

            float centerX = node.x + width * 0.5f;
            float centerY = psdData.height - (node.y + height * 0.5f);
            Vector3 worldCenterPos = root.TransformPoint(new Vector3(centerX - psdData.width * 0.5f, centerY - psdData.height * 0.5f, 0));
            float pivotOffsetX = (rt.pivot.x - 0.5f) * width;
            float pivotOffsetY = (rt.pivot.y - 0.5f) * height;
            Vector3 worldPivotOffset = rt.TransformVector(new Vector3(pivotOffsetX, pivotOffsetY, 0));
            Vector3 worldPos = worldCenterPos + worldPivotOffset;

            RectTransform parentRt = rt.parent as RectTransform;
            if (parentRt == null)
            {
                rt.position = worldPos;
            }
            else
            {
                Vector3 localPos = parentRt.InverseTransformPoint(worldPos);
                SetAnchoredPositionFromLocal(rt, parentRt, localPos);
            }
        }

        public static void ApplyPsdLayoutChildOrder(Transform layoutTransform, PicData layoutItem, PSDData psdData)
        {
            if (layoutTransform == null ||
                psdData == null ||
                layoutItem.id <= 0)
            {
                return;
            }

            if (PSDScrollRectUtility.IsScrollRectRoot(layoutItem))
            {
                RectTransform content = PSDScrollRectUtility.GetContentTransform(layoutTransform);
                if (content == null ||
                    !PSDScrollRectUtility.TryGetContentLayoutData(layoutItem, psdData, out PicData contentLayout))
                {
                    return;
                }

                ApplyPsdLayoutChildOrderCore(content, contentLayout, PSDScrollRectUtility.GetContentLayoutChildren(layoutItem, psdData));
                return;
            }

            if (string.IsNullOrEmpty(layoutItem.layoutType) ||
                string.Equals(layoutItem.layoutType, "None", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ApplyPsdLayoutChildOrderCore(layoutTransform, layoutItem, GetDirectLayoutChildren(layoutItem, psdData));
        }

        private static void ApplyPsdLayoutChildOrderCore(Transform layoutTransform, PicData layoutItem, List<PicData> children)
        {
            RectTransform layoutRt = layoutTransform as RectTransform;
            if (layoutRt == null || layoutTransform.childCount <= 1)
            {
                return;
            }

            if (children.Count <= 1)
            {
                return;
            }

            SortLayoutChildren(children, layoutItem.layoutType);

            HashSet<Transform> used = new HashSet<Transform>();
            int siblingIndex = 0;
            for (int i = 0; i < children.Count; i++)
            {
                Transform child = FindDirectChildByPsdItem(layoutTransform, children[i], used);
                if (child == null)
                {
                    continue;
                }

                used.Add(child);
                Undo.RecordObject(child, "Sync Layout Child Order");
                child.SetSiblingIndex(siblingIndex++);
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(layoutRt);
            Canvas.ForceUpdateCanvases();
        }

        private static List<PicData> GetDirectLayoutChildren(PicData layoutItem, PSDData psdData)
        {
            List<PicData> children = new List<PicData>();
            if (psdData == null || psdData.listPngData == null)
            {
                return children;
            }

            for (int i = 0; i < psdData.listPngData.Count; i++)
            {
                PicData child = psdData.listPngData[i];
                if (child.id == layoutItem.id || child.excludeFromRestore)
                {
                    continue;
                }

                if (child.hasParent && child.parentNodeId == layoutItem.id)
                {
                    children.Add(child);
                }
            }

            return children;
        }

        private static void SortLayoutChildren(List<PicData> children, string layoutType)
        {
            if (children == null)
            {
                return;
            }

            if (string.Equals(layoutType, "Horizontal", StringComparison.OrdinalIgnoreCase))
            {
                children.Sort((a, b) => a.x.CompareTo(b.x));
            }
            else if (string.Equals(layoutType, "Vertical", StringComparison.OrdinalIgnoreCase))
            {
                children.Sort((a, b) => b.y.CompareTo(a.y));
            }
            else if (string.Equals(layoutType, "Grid", StringComparison.OrdinalIgnoreCase))
            {
                children.Sort((a, b) =>
                {
                    int y = b.y.CompareTo(a.y);
                    return y != 0 ? y : a.x.CompareTo(b.x);
                });
            }
        }

        private static Transform FindDirectChildByPsdItem(Transform parent, PicData item, HashSet<Transform> used)
        {
            if (parent == null)
            {
                return null;
            }

            string cleanName = NormalizeUnityName(item.cleanName);
            string pngName = NormalizeUnityName(item.pngName);

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child == null || (used != null && used.Contains(child)))
                {
                    continue;
                }

                string childName = NormalizeUnityName(child.name);
                if (NameEquals(childName, cleanName) || NameEquals(childName, pngName))
                {
                    return child;
                }
            }

            return null;
        }

        private static bool NameEquals(string a, string b)
        {
            return !string.IsNullOrEmpty(a) &&
                   !string.IsNullOrEmpty(b) &&
                   string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyPsdPositionPreserveCurrentRect(Transform target, Transform root, PicData item, PSDData psdData)
        {
            RectTransform rt = target.GetComponent<RectTransform>();
            if (rt == null) return;
            RectTransform parentRt = target.parent as RectTransform;

            float currentWidth = rt.rect.width > 0f ? rt.rect.width : rt.sizeDelta.x;
            float currentHeight = rt.rect.height > 0f ? rt.rect.height : rt.sizeDelta.y;
            if (currentWidth <= 0f) currentWidth = item.width;
            if (currentHeight <= 0f) currentHeight = item.height;

            float psdCenterX = item.x - psdData.width * 0.5f;
            float psdCenterY = item.y - psdData.height * 0.5f;
            Vector3 worldCenterPos = root.TransformPoint(new Vector3(psdCenterX, psdCenterY, 0));

            float pivotOffsetX = (rt.pivot.x - 0.5f) * currentWidth;
            float pivotOffsetY = (rt.pivot.y - 0.5f) * currentHeight;
            Vector3 worldPivotOffset = target.TransformVector(new Vector3(pivotOffsetX, pivotOffsetY, 0));

            Vector3 worldPos = worldCenterPos + worldPivotOffset;
            if (parentRt == null)
            {
                target.position = worldPos;
            }
            else
            {
                Vector3 localPos = parentRt.InverseTransformPoint(worldPos);
                SetAnchoredPositionFromLocal(rt, parentRt, localPos);
            }
        }

        private static void ApplyPsdPositionLocal(Transform target, PicData item, PicData parentItem)
        {
            RectTransform rt = target.GetComponent<RectTransform>();
            RectTransform parentRt = target.parent as RectTransform;
            if (rt == null || parentRt == null) return;

            float localX = item.x - parentItem.x;
            float localY = item.y - parentItem.y;

            float parentPivotOffsetX = (parentRt.pivot.x - 0.5f) * parentItem.width;
            float parentPivotOffsetY = (parentRt.pivot.y - 0.5f) * parentItem.height;

            float pivotOffsetX = (rt.pivot.x - 0.5f) * item.width;
            float pivotOffsetY = (rt.pivot.y - 0.5f) * item.height;

            Vector3 localPos = new Vector3(
                localX - parentPivotOffsetX + pivotOffsetX,
                localY - parentPivotOffsetY + pivotOffsetY,
                rt.localPosition.z);

            SetAnchoredPositionFromLocal(rt, parentRt, localPos);
        }

        private static void SetAnchoredPositionFromLocal(RectTransform rt, RectTransform parentRt, Vector3 localPos)
        {
            if (rt == null || parentRt == null)
            {
                if (rt != null) rt.localPosition = localPos;
                return;
            }

            Vector2 parentSize = parentRt.rect.size;
            Vector2 anchorRef = (rt.anchorMin + rt.anchorMax) * 0.5f;
            Vector2 anchorOffset = Vector2.Scale(anchorRef - parentRt.pivot, parentSize);
            Vector2 anchoredPos = new Vector2(localPos.x, localPos.y) - anchorOffset;
            rt.anchoredPosition = anchoredPos;

            var lp = rt.localPosition;
            rt.localPosition = new Vector3(lp.x, lp.y, localPos.z);
        }

        private static bool IsChildOfLayoutItemContainer(Transform t)
        {
            if (t == null || t.parent == null || t.parent.parent == null) return false;
            return HasEnabledLayoutGroup(t.parent.parent);
        }

        private static bool IsControlledByPsdLayoutParent(Transform target, PicData item, PSDData psdData)
        {
            if (target == null || target.parent == null || !HasEnabledLayoutGroup(target.parent))
            {
                return false;
            }

            if (psdData != null && psdData.HasSkeleton)
            {
                if (PSDScrollRectUtility.IsInsideScrollRect(item, psdData))
                {
                    return true;
                }

                return item.hasParent &&
                       TryGetSkeletonNode(psdData, item.parentNodeId, out PsdSkeletonNode parentNode) &&
                       IsPsdLayoutNode(parentNode);
            }

            return true;
        }

        private static bool HasEnabledLayoutGroup(Transform target)
        {
            LayoutGroup layout = target != null ? target.GetComponent<LayoutGroup>() : null;
            return layout != null && layout.isActiveAndEnabled;
        }

        private static bool IsPsdLayoutNode(PsdSkeletonNode node)
        {
            if (node == null)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(node.layoutHint) &&
                !string.Equals(node.layoutHint, "None", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(node.uiTypeHint, "Horizontal", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(node.uiTypeHint, "Vertical", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(node.uiTypeHint, "Grid", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetParentPsdData(Transform parent, PicData child, PSDData psdData, out PicData parentData)
        {
            parentData = default;
            if (parent == null || psdData == null || psdData.listPngData == null) return false;

            string parentName = NormalizeUnityName(parent.name);
            if (string.IsNullOrEmpty(parentName)) return false;

            List<PicData> nameMatchedItems = null;
            for (int i = 0; i < psdData.listPngData.Count; i++)
            {
                var data = psdData.listPngData[i];
                if (data.uiType != "Item") continue;

                string cleanName = NormalizeUnityName(data.cleanName);
                string pngName = NormalizeUnityName(data.pngName);
                if (string.Equals(cleanName, parentName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pngName, parentName, StringComparison.OrdinalIgnoreCase))
                {
                    if (nameMatchedItems == null) nameMatchedItems = new List<PicData>();
                    nameMatchedItems.Add(data);
                }
            }

            if (nameMatchedItems == null || nameMatchedItems.Count == 0) return false;

            if (nameMatchedItems.Count == 1)
            {
                parentData = nameMatchedItems[0];
                return true;
            }

            PicData best = default;
            bool found = false;
            float bestArea = float.MaxValue;
            for (int i = 0; i < nameMatchedItems.Count; i++)
            {
                var data = nameMatchedItems[i];
                if (!IsInside(child, data)) continue;

                float area = data.width * data.height;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = data;
                    found = true;
                }
            }

            if (found)
            {
                parentData = best;
                return true;
            }

            return false;
        }

        private static string NormalizeUnityName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            string trimmed = name.Trim();
            int idx = trimmed.LastIndexOf(" (", StringComparison.Ordinal);
            if (idx > 0 && trimmed.EndsWith(")", StringComparison.Ordinal))
            {
                string suffix = trimmed.Substring(idx + 2, trimmed.Length - idx - 3);
                if (int.TryParse(suffix, out _))
                {
                    return trimmed.Substring(0, idx);
                }
            }
            return trimmed;
        }

        private static bool IsInside(PicData child, PicData parent)
        {
            float left = parent.x - parent.width / 2f;
            float right = parent.x + parent.width / 2f;
            float bottom = parent.y - parent.height / 2f;
            float top = parent.y + parent.height / 2f;
            return child.x >= left && child.x <= right && child.y >= bottom && child.y <= top;
        }

        // --- 组件 Setup 方法 ---

        public static void SetupText(UnityEngine.UI.Text txt, PicData item, PSDImportConfig config)
        {
            SetupTextInternal(
                txt,
                item.textContent,
                item.fontSize,
                item.fontColor,
                item.textOpacity,
                item.lineSpacing,
                item.textAlign,
                config);
        }

        private static void SetupTextInternal(
            UnityEngine.UI.Text txt,
            string textContent,
            float fontSize,
            Color fontColor,
            float textOpacity,
            float lineSpacing,
            string textAlign,
            PSDImportConfig config)
        {
            txt.text = textContent;
            txt.fontSize = Mathf.RoundToInt(fontSize);
            txt.color = fontColor;
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.supportRichText = true;

            // 文本对齐映射：PS justification → Unity TextAnchor
            // PS 只有水平对齐，垂直保持居中
            switch (textAlign)
            {
                case "left":
                    txt.alignment = TextAnchor.MiddleLeft;
                    break;
                case "right":
                    txt.alignment = TextAnchor.MiddleRight;
                    break;
                case "center":
                default:
                    txt.alignment = TextAnchor.MiddleCenter;
                    break;
            }

            // 不透明度映射：PS 图层 opacity(0~100) → Unity Color.alpha(0~1)
            if (textOpacity < 100f)
            {
                var c = txt.color;
                c.a = Mathf.Clamp01(textOpacity / 100f);
                txt.color = c;
            }

            // 行间距映射：PS leading(px) → Unity lineSpacing(倍率)
            // Unity lineSpacing = 1 为默认行高；PS leading 包含字号+间距
            // 换算：lineSpacingMultiplier = leading / fontSize
            if (lineSpacing > 0 && fontSize > 0)
            {
                txt.lineSpacing = lineSpacing / fontSize;
            }

            if (config != null && config.defaultTextFont != null)
            {
                txt.font = config.defaultTextFont;
            }
            else if (txt.font == null)
            {
                txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }
            //txt.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, item.width);
            //txt.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, item.height);
            // 注意：不要在这里再次 SetSize，因为外面已经 Set 过了
        }

        /// <summary>
        /// 挂载 UGUI 原生 Outline 描边效果。
        /// 描边宽度映射：strokeSize(px) → effectDistance(x=y 统一值)
        /// </summary>
        private static void SetupTextOutline(GameObject go, PicData item)
        {
            var outline = go.GetComponent<Outline>() ?? go.AddComponent<Outline>();
            float outlineWidth = Mathf.Max(0.5f, item.strokeSize);
            outline.effectColor = item.strokeColor;
            outline.effectDistance = new Vector2(outlineWidth, outlineWidth);
            outline.useGraphicAlpha = true;
        }

        /// <summary>
        /// 渐变兜底：UGUI 原生无渐变 effect，用 Shadow 取渐变底色做投影，垂直偏移。
        /// 仅保留视觉层次感，不还原真实渐变方向。
        /// </summary>
        private static void SetupTextGradientFallback(GameObject go, PicData item)
        {
            var shadow = go.GetComponent<Shadow>() ?? go.AddComponent<Shadow>();
            shadow.effectColor = new Color(item.gradientBottomColor.r, item.gradientBottomColor.g, item.gradientBottomColor.b, 0.6f);
            shadow.effectDistance = new Vector2(0f, Mathf.Max(1f, item.fontSize * 0.1f));
            shadow.useGraphicAlpha = true;
        }


        // --- 文件名回退查找缓存（同一次 Create/Sync 调用内复用，避免重复扫描） ---
        private static Dictionary<string, string> _filenamePathCache;

        /// <summary>
        /// 在 assetFolder 下递归搜索与 pngName 匹配的 .png 文件（Unity 项目相对路径）。
        /// 用于精确路径找不到时的回退：切图和 .ps.data 同目录但名字唯一时可通过文件名匹配。
        /// </summary>
        internal static string FindPngByFilename(string pngName, string assetFolder)
        {
            if (string.IsNullOrEmpty(pngName) || string.IsNullOrEmpty(assetFolder)) return null;
            // trim: 防 JSX 端残留前后空白（旧 .ps.data 数据兼容）
            string targetFileName = pngName.Trim() + ".png";

            if (_filenamePathCache == null) _filenamePathCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_filenamePathCache.TryGetValue(targetFileName, out var cached)) return cached;

            // 扫描范围：assetFolder 及其所有子目录
            string absFolder = PSDAssetDeduper.GetAbsolutePath(assetFolder);
            if (!Directory.Exists(absFolder))
            {
                _filenamePathCache[targetFileName] = null;
                return null;
            }

            try
            {
                var files = Directory.GetFiles(absFolder, targetFileName, SearchOption.AllDirectories);
                foreach (var f in files)
                {
                    // 将绝对路径转回 Unity 相对路径
                    string relative = f.Replace("\\", "/");
                    int idx = relative.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        relative = relative.Substring(idx);
                        _filenamePathCache[targetFileName] = relative;
                        return relative;
                    }
                }
            }
            catch (System.Exception) { /* 权限等问题静默忽略 */ }

            _filenamePathCache[targetFileName] = null;
            return null;
        }

        private static void LoadSpriteFromPath(UnityEngine.UI.Image img, PicData item, string resolvedPath, bool hasSlice, Vector4 sliceBorder, PSDImportConfig config)
        {
            Sprite sp = AssetDatabase.LoadAssetAtPath<Sprite>(resolvedPath);
            if (sp != null)
            {
                img.sprite = sp;
                img.type = hasSlice ? Image.Type.Sliced : Image.Type.Simple;
                img.color = Color.white; // 颜色清洗
            }
        }

        private static bool SetupImageWithReuse(UnityEngine.UI.Image img, PicData item, string assetFolder, PSDImportConfig config)
        {
            string pngpath = PSDImageReuseLogStore.BuildPngPath(item, assetFolder);
            bool skipAutoSlice = PSDTagUtility.HasTag(item.pngName, "@Bg");

            string resolvedPath = PSDAssetDeduper.GetCanonicalPath(
                pngpath,
                config != null && config.dedupeSprites,
                config != null && config.dedupeMoveDuplicates,
                config != null ? config.dedupeMoveFolder : null,
                assetFolder);
            string resolvedAbs = PSDAssetDeduper.GetAbsolutePath(resolvedPath);
            var localResult = new PSDImageReuseResult
            {
                sourceKind = string.Equals(resolvedPath, pngpath, StringComparison.OrdinalIgnoreCase)
                    ? PSDImageReuseSourceKind.OriginalExport
                    : PSDImageReuseSourceKind.LocalDuplicate,
                originalExportPath = pngpath,
                resolvedAssetPath = resolvedPath,
                canonicalExportPath = resolvedPath,
                spritePath = resolvedPath,
                confidence = 1f,
                matchReason = string.Equals(resolvedPath, pngpath, StringComparison.OrdinalIgnoreCase)
                    ? "original export"
                    : "local duplicate visual hash"
            };

            bool hasSlice = item.hasSlice;
            Vector4 sliceBorder = item.sliceBorder;

            if (File.Exists(resolvedAbs))
            {
                if (!hasSlice && config != null && config.autoSlice && !skipAutoSlice)
                {
                    if (PSDNineSliceUtility.TryDetectBorder(resolvedAbs, out var detectedBorder))
                    {
                        hasSlice = true;
                        sliceBorder = detectedBorder;
                    }
                }
                if (hasSlice)
                {
                    FixSpriteImport(resolvedPath, sliceBorder);
                }
                LoadSpriteFromPath(img, item, resolvedPath, hasSlice, sliceBorder, config);
                localResult.hasSlice = hasSlice;
                localResult.sliceBorder = sliceBorder;
                localResult.diagnostics = skipAutoSlice ? "autoSlice skipped by @Bg" : null;
                PSDImageReuseLogStore.Record(item, localResult);
                return true;
            }

            string fallbackPath = FindPngByFilename(item.pngName, assetFolder);
            if (!string.IsNullOrEmpty(fallbackPath))
            {
                if (!hasSlice && config != null && config.autoSlice && !skipAutoSlice)
                {
                    string fallbackAbs = PSDAssetDeduper.GetAbsolutePath(fallbackPath);
                    if (File.Exists(fallbackAbs) && PSDNineSliceUtility.TryDetectBorder(fallbackAbs, out var fbBorder))
                    {
                        hasSlice = true;
                        sliceBorder = fbBorder;
                    }
                }
                if (hasSlice) FixSpriteImport(fallbackPath, sliceBorder);
                LoadSpriteFromPath(img, item, fallbackPath, hasSlice, sliceBorder, config);
                localResult.sourceKind = PSDImageReuseSourceKind.OriginalExport;
                localResult.resolvedAssetPath = fallbackPath;
                localResult.canonicalExportPath = fallbackPath;
                localResult.spritePath = fallbackPath;
                localResult.matchReason = "filename fallback";
                localResult.hasSlice = hasSlice;
                localResult.sliceBorder = sliceBorder;
                localResult.diagnostics = skipAutoSlice ? "autoSlice skipped by @Bg" : null;
                PSDImageReuseLogStore.Record(item, localResult);
                return true;
            }

            localResult.sourceKind = PSDImageReuseSourceKind.Missing;
            localResult.rejectReason = "png not found";
            PSDImageReuseLogStore.Record(item, localResult);
            OnPicMissing?.Invoke(img.gameObject, pngpath);
            return true;
        }

        public static void SetupImage(UnityEngine.UI.Image img, PicData item, string assetFolder, PSDImportConfig config)
        {
            if (SetupImageWithReuse(img, item, assetFolder, config))
            {
                return;
            }

            string pngpath = PSDImageReuseLogStore.BuildPngPath(item, assetFolder);

            string resolvedPath = PSDAssetDeduper.GetCanonicalPath(
                pngpath,
                config != null && config.dedupeSprites,
                config != null && config.dedupeMoveDuplicates,
                config != null ? config.dedupeMoveFolder : null,
                assetFolder);
            string resolvedAbs = PSDAssetDeduper.GetAbsolutePath(resolvedPath);

            bool hasSlice = item.hasSlice;
            Vector4 sliceBorder = item.sliceBorder;

            if (!hasSlice && config != null && config.autoSlice)
            {
                // autoSlice 需要在最终确定路径后检测
            }

            if (File.Exists(resolvedAbs))
            {
                if (!hasSlice && config != null && config.autoSlice)
                {
                    if (PSDNineSliceUtility.TryDetectBorder(resolvedAbs, out var detectedBorder))
                    {
                        hasSlice = true;
                        sliceBorder = detectedBorder;
                    }
                }
                if (hasSlice)
                {
                    FixSpriteImport(resolvedPath, sliceBorder);
                }
                LoadSpriteFromPath(img, item, resolvedPath, hasSlice, sliceBorder, config);
            }
            else
            {
                // 回退：按文件名在资产目录递归查找（切图和 .ps.data 同级目录场景）
                string fallbackPath = FindPngByFilename(item.pngName, assetFolder);
                if (!string.IsNullOrEmpty(fallbackPath))
                {
                    if (!hasSlice && config != null && config.autoSlice)
                    {
                        string fallbackAbs = PSDAssetDeduper.GetAbsolutePath(fallbackPath);
                        if (File.Exists(fallbackAbs) && PSDNineSliceUtility.TryDetectBorder(fallbackAbs, out var fbBorder))
                        {
                            hasSlice = true;
                            sliceBorder = fbBorder;
                        }
                    }
                    if (hasSlice) FixSpriteImport(fallbackPath, sliceBorder);
                    LoadSpriteFromPath(img, item, fallbackPath, hasSlice, sliceBorder, config);
                }
                else
                {
                    OnPicMissing?.Invoke(img.gameObject, pngpath);
                }
            }
        }

        // --- 通用辅助 ---

        private static T EnsureComponent<T>(GameObject go) where T : UnityEngine.Component
        {
            T comp = go.GetComponent<T>();
            if (comp == null)
            {
                // 互斥清理
                RemoveConflictingUiComponents<T>(go);
                comp = go.AddComponent<T>();
            }
            return comp;
        }

        private static void RemoveConflictingUiComponents<T>(GameObject go) where T : UnityEngine.Component
        {
            if (typeof(T) == typeof(UnityEngine.UI.Image)) { DestroyIfExists<UnityEngine.UI.Text>(go); }
            else if (typeof(T) == typeof(UnityEngine.UI.Text)) { DestroyIfExists<UnityEngine.UI.Image>(go); }
        }

        private static void DestroyIfExists<T>(GameObject go) where T : UnityEngine.Component
        {
            var c = go.GetComponent<T>();
            if (c != null) UnityEngine.Object.DestroyImmediate(c);
        }

        private static void FixSpriteImport(string path, Vector4? spriteBorder)
        {
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                bool dirty = false;
                if (importer.textureType != TextureImporterType.Sprite) { importer.textureType = TextureImporterType.Sprite; dirty = true; }
                if (importer.spriteImportMode != SpriteImportMode.Single) { importer.spriteImportMode = SpriteImportMode.Single; dirty = true; }
                if (spriteBorder.HasValue)
                {
                    Vector4 border = spriteBorder.Value;
                    if (!Approximately(importer.spriteBorder, border))
                    {
                        importer.spriteBorder = border;
                        dirty = true;
                    }
                }
                if (dirty) importer.SaveAndReimport();
            }
        }

        private static bool Approximately(Vector4 a, Vector4 b)
        {
            return Mathf.Abs(a.x - b.x) < 0.01f &&
                   Mathf.Abs(a.y - b.y) < 0.01f &&
                   Mathf.Abs(a.z - b.z) < 0.01f &&
                   Mathf.Abs(a.w - b.w) < 0.01f;
        }

        private static RectTransform CreateCanvasRoot(string name)
        {
            var go = new GameObject(string.IsNullOrEmpty(name) ? "Canvas" : name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0) go.layer = uiLayer;

            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;
            scaler.referencePixelsPerUnit = 100f;

            return go.GetComponent<RectTransform>();
        }

        private static T CreateGo<T>(string goName, Transform parent, string LayerName = null) where T : UnityEngine.Component
        {
            GameObject go = new GameObject(goName);
            if (!string.IsNullOrEmpty(LayerName)) go.layer = LayerMask.NameToLayer(LayerName);
            if (parent != null) go.transform.SetParent(parent, false);
            return go.AddComponent<T>();
        }

        private static RectTransform BuildHierarchy(RectTransform root, string groupName)
        {
            RectTransform current = root;
            if (groupName != "root/" && groupName != "/")
            {
                var paths = groupName.Split(new char[] { '/' }, System.StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in paths)
                {
                    var child = current.Find(p) as RectTransform;
                    if (child == null)
                    {
                        child = CreateGo<RectTransform>(p, current, "UI");
                        child.transform.localPosition = Vector3.zero;
                        child.anchorMin = Vector2.zero;
                        child.anchorMax = Vector2.one;
                        child.offsetMin = Vector2.zero;
                        child.offsetMax = Vector2.zero;
                    }
                    current = child;
                }
            }
            return current;
        }

        private static Vector2 PsdTopLeftToAnchored(float xTopLeft, float yTopLeft, float width, float height, int canvasWidth, int canvasHeight)
        {
            float centerX = xTopLeft + width * 0.5f;
            float centerYFromTop = yTopLeft + height * 0.5f;
            float anchoredX = centerX - canvasWidth * 0.5f;
            float anchoredY = canvasHeight * 0.5f - centerYFromTop;
            return new Vector2(anchoredX, anchoredY);
        }
    }
}
