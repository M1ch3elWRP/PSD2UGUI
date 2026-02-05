using System;
using System.Collections.Generic;
using System.IO;
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

        // =========================================================================
        // 生成模式 (Generate)
        // =========================================================================
        public static void CreateUGUI_GenerateMode(PSDData psdData, PSDImportConfig config)
        {
            AssetDatabase.Refresh();
            PSDAssetDeduper.EnsureScope(psdData.psdAssetsFolder);
            var rootRectTrans = CreateCanvasRoot(new DirectoryInfo(psdData.psdAssetsFolder).Name);

            rootRectTrans.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, psdData.width);
            rootRectTrans.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, psdData.height);
            rootRectTrans.anchoredPosition = Vector2.zero;
            rootRectTrans.localScale = Vector3.one;

            foreach (var item in psdData.listPngData)
            {
                RectTransform tranParent = BuildHierarchy(rootRectTrans, item.groupName);
                GameObject createdGo = CreateNodeObject(item, tranParent, config);

                // 刷新数据
                RefreshNode(createdGo, item, psdData, true, config);

                OnNodeCreated?.Invoke(createdGo, item.pngName);
            }
            Debug.Log("<color=cyan>[PSD Create] 生成完毕。</color>");
        }

        // =========================================================================
        // 同步模式 (Sync) - 这里的入口主要用于旧菜单，窗口模式主要调用 RefreshNode
        // =========================================================================
        public static void CreateUGUI_SyncMode(PSDData psdData, Transform root, PSDImportConfig config)
        {
            AssetDatabase.Refresh();
            PSDAssetDeduper.EnsureScope(psdData.psdAssetsFolder);
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
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, item.width);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, item.height);

            if (updatePosition)
            {
                bool controlledByLayout = go.transform.parent != null &&
                                          go.transform.parent.GetComponent<LayoutGroup>() != null;
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
            }

            // 2. 组件挂载 (Case Item 修改)
            switch (item.uiType)
            {
                case "Text":
                    var txt = EnsureComponentWithOverride<Text>(go, config != null ? config.textComponent : null);
                    SetupText(txt, item, config);
                    break;

                case "Button":
                    EnsureComponentWithOverride<Button>(go, config != null ? config.buttonComponent : null);
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

                case "RawImage":
                    if (item.layoutType == "None")
                    {
                        var raw = EnsureComponentWithOverride<RawImage>(go, config != null ? config.rawImageComponent : null);
                        SetupRawImage(raw, item, psdData.psdAssetsFolder, config);
                    }
                    break;

                case "Image":
                default:
                    if (item.layoutType == "None")
                    {
                        var img = EnsureComponentWithOverride<Image>(go, config != null ? config.imageComponent : null);
                        SetupImage(img, item, psdData.psdAssetsFolder, config);
                    }
                    break;
            }

            // 3. 处理 LayoutGroup 逻辑 (核心修改)
            if (item.layoutType != "None")
            {
                // A. 收集 PSD 里属于该 Layout 的子数据 (用于计算 Padding/Spacing)
                List<PicData> layoutChildren = new List<PicData>();

                // 简单粗暴：遍历 PSD 数据，找到几何中心位于当前 Layout 范围内的项
                foreach (var other in psdData.listPngData)
                {
                    if (other.id == item.id) continue;
                    // 只认 @Item, @Image, @Button 作为子元素
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

        private static void HandleItemPool(GameObject go, PicData item)
        {
            // 尝试获取 UIItemPool 组件 (需要你的项目中引用了 TZ.Framework.UGUI)
            var poolComp = go.GetComponent("UIItemPool");
            // 如果不想用反射或者字符串，请在此处引用你的命名空间并使用:
            // var poolComp = go.GetComponent<TZ.Framework.UGUI.UIItemPool>();

            if (poolComp != null)
            {
                // 这里用反射来赋值，以免在这个脚本里产生对具体项目代码的强依赖
                // 如果你确定引用了命名空间，可以直接写 poolComp.itemPrefab

                // 1. 尝试找到 Item 模板
                // 假设 UIItemPool 有个 itemPrefab 字段
                var itemPrefabField = poolComp.GetType().GetField("itemPrefab");
                Component itemPrefabLink = itemPrefabField?.GetValue(poolComp) as Component;

                GameObject templateGo = null;
                if (itemPrefabLink != null)
                {
                    templateGo = itemPrefabLink.gameObject;
                }
                else if (go.transform.childCount > 0)
                {
                    // 没赋值引用，找第一个子节点
                    templateGo = go.transform.GetChild(0).gameObject;
                }

                // 2. 同步尺寸到模板
                if (templateGo != null)
                {
                    RectTransform itemRT = templateGo.GetComponent<RectTransform>();
                    if (itemRT != null)
                    {
                        Undo.RecordObject(itemRT, "Sync Item Size");
                        itemRT.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, item.width);
                        itemRT.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, item.height);
                    }
                }
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
            return t.parent.parent.GetComponent<LayoutGroup>() != null;
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

        private static bool IsCommonTagged(PicData item)
        {
            if (string.IsNullOrEmpty(item.pngName)) return false;
            return item.pngName.IndexOf("@Common", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // --- 组件 Setup 方法 ---

        public static void SetupText(UnityEngine.UI.Text txt, PicData item, PSDImportConfig config)
        {
            txt.text = item.textContent;
            txt.fontSize = Mathf.RoundToInt(item.fontSize);
            txt.color = item.fontColor;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.alignment = TextAnchor.MiddleCenter;
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

        public static void SetupImage(UnityEngine.UI.Image img, PicData item, string assetFolder, PSDImportConfig config)
        {
            string group = item.groupName == "root/" ? "/" : item.groupName;
            string folder = assetFolder.Replace("\\", "/");
            string pngpath = $"{folder}{group}{item.pngName}.png".Replace("//", "/");

            if (config != null && config.commonSpriteMatch && IsCommonTagged(item))
            {
                if (PSDCommonSpriteMatcher.TryResolveCommonSprite(pngpath, config, out var commonSprite))
                {
                    img.sprite = commonSprite;
                    img.type = (commonSprite != null && commonSprite.border.sqrMagnitude > 0f) ? Image.Type.Sliced : Image.Type.Simple;
                    PSDCommonSpriteMatcher.MoveMatchedExport(pngpath, assetFolder, config);
                    return;
                }
            }

            string resolvedPath = PSDAssetDeduper.GetCanonicalPath(
                pngpath,
                config != null && config.dedupeSprites,
                config != null && config.dedupeMoveDuplicates,
                config != null ? config.dedupeMoveFolder : null,
                assetFolder);
            string resolvedAbs = PSDAssetDeduper.GetAbsolutePath(resolvedPath);

            if (File.Exists(resolvedAbs))
            {
                bool hasSlice = item.hasSlice;
                Vector4 sliceBorder = item.sliceBorder;

                if (item.pngName.IndexOf("@Bg", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hasSlice = false;
                }
                else if (!hasSlice && config != null && config.autoSlice)
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
                Sprite sp = AssetDatabase.LoadAssetAtPath<Sprite>(resolvedPath);
                if (sp != null)
                {
                    img.sprite = sp;
                    if (hasSlice) img.type = Image.Type.Sliced;
                    img.color = Color.white; // 颜色清洗
                }
            }
            else
            {
                // 如果找不到图，不要变红，可能是纯容器，保持透明或白色
                //img.color = new Color(1, 0, 0, 0.5f);
                OnPicMissing?.Invoke(img.gameObject, pngpath);
            }
        }

        public static void SetupRawImage(UnityEngine.UI.RawImage raw, PicData item, string assetFolder, PSDImportConfig config)
        {
            string group = item.groupName == "root/" ? "/" : item.groupName;
            string folder = assetFolder.Replace("\\", "/");
            string pngpath = $"{folder}{group}{item.pngName}.png".Replace("//", "/");
            string resolvedPath = PSDAssetDeduper.GetCanonicalPath(
                pngpath,
                config != null && config.dedupeSprites,
                config != null && config.dedupeMoveDuplicates,
                config != null ? config.dedupeMoveFolder : null,
                assetFolder);
            string resolvedAbs = PSDAssetDeduper.GetAbsolutePath(resolvedPath);

            if (File.Exists(resolvedAbs))
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture>(resolvedPath);
                if (tex != null)
                {
                    raw.texture = tex;
                    raw.color = Color.white;
                }
            }
        }

        // --- 通用辅助 ---

        private static T EnsureComponentWithOverride<T>(GameObject go, MonoScript overrideScript) where T : UnityEngine.Component
        {
            var overrideType = GetOverrideType<T>(overrideScript);
            if (overrideType != null)
            {
                RemoveConflictingUiComponents<T>(go);
                var comp = go.GetComponent(overrideType) as T;
                if (comp == null) comp = go.AddComponent(overrideType) as T;
                return comp;
            }
            return EnsureComponent<T>(go);
        }

        private static System.Type GetOverrideType<T>(MonoScript script) where T : UnityEngine.Component
        {
            if (script == null) return null;
            var type = script.GetClass();
            if (type == null) return null;
            if (!typeof(T).IsAssignableFrom(type)) return null;
            return type;
        }

        private static void RemoveConflictingUiComponents<T>(GameObject go) where T : UnityEngine.Component
        {
            if (typeof(T) == typeof(UnityEngine.UI.RawImage)) { DestroyIfExists<UnityEngine.UI.Image>(go); DestroyIfExists<UnityEngine.UI.Text>(go); }
            else if (typeof(T) == typeof(UnityEngine.UI.Image)) { DestroyIfExists<UnityEngine.UI.RawImage>(go); DestroyIfExists<UnityEngine.UI.Text>(go); }
            else if (typeof(T) == typeof(UnityEngine.UI.Text)) { DestroyIfExists<UnityEngine.UI.Image>(go); DestroyIfExists<UnityEngine.UI.RawImage>(go); }
        }

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
    }
}
