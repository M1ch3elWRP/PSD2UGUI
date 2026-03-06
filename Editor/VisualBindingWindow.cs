using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityObject = UnityEngine.Object;
using System;

namespace PSDImporter
{
    // --- ViewModel 定义 (保持不变) ---
    public class BindingPairViewModel
    {
        public PicData psdItem;
        public Transform unityNode;
        public float score;
        public bool isConfirmed;
        public string statusInfo;
        public bool isIdMatched;
        public int depth;
    }

    public class PrefabNodeViewModel
    {
        public Transform transform;
        public int depth;
        public bool isBound;
    }

    public class VisualBindingWindow : EditorWindow
    {
        // --- 资源引用 ---
        private UnityObject psdDataFile;
        private GameObject targetRoot;
        private PSDImportConfig config;

        // --- 数据 ---
        private PSDData cachedPsdData;
        private PSDBindingData bindingAsset;
        private List<BindingPairViewModel> bindings = new List<BindingPairViewModel>();
        private List<PrefabNodeViewModel> prefabNodes = new List<PrefabNodeViewModel>();

        // --- 缓存与交互 ---
        private Dictionary<string, Texture2D> textureCache = new Dictionary<string, Texture2D>();
        private BindingPairViewModel selectedBinding = null;
        private Transform selectedPrefabNode = null;

        // --- 视图状态 ---
        private Vector2 scrollPosBinding;
        private Vector2 scrollPosPrefab;
        private float previewZoom = 0.2f;
        private Vector2 previewPan = Vector2.zero;
        private float bindingViewHeight;
        private float prefabViewHeight;
        private bool pendingScrollToBinding;
        private bool pendingScrollToPrefab;
        private readonly Dictionary<int, bool> prefabFoldout = new Dictionary<int, bool>();

        // --- 【布局核心参数】 ---
        private float previewWidth = 450f;
        private float bindingListWidth = 400f;

        private static readonly Color PsdOutlineColor = new Color(0.25f, 0.65f, 1f, 0.7f);
        private static readonly Color PrefabOutlineColor = new Color(1f, 0.7f, 0.2f, 0.75f);


        // 拖拽状态
        private bool isResizingPreview = false;
        private bool isResizingList = false;

        // 常量配置
        private const float MIN_WIDTH = 200f;   // 任何面板的最小宽度
        private const float SPLITTER_W = 4f;    // 分割线宽度
        private const float TOOLBAR_H = 20f;    // 顶部高度
        private const float BOTTOM_H = 30f;     // 底部高度

        [MenuItem("PSDTools/Restore UI From PSD", priority = 1)]
        public static void ShowWindow()
        {
            var win = GetWindow<VisualBindingWindow>("PSD Restore");
            win.minSize = new Vector2(800, 600);
            win.Show();
        }

        private void OnEnable()
        {
            if (config == null)
            {
                string[] guids = AssetDatabase.FindAssets("t:PSDImportConfig");
                if (guids.Length > 0)
                    config = AssetDatabase.LoadAssetAtPath<PSDImportConfig>(AssetDatabase.GUIDToAssetPath(guids[0]));
                else
                    config = CreateInstance<PSDImportConfig>();
            }
        }

        // =================================================================================
        // 核心 GUI 渲染循环 (使用绝对定位布局)
        // =================================================================================
        private void OnGUI()
        {
            // 1. 处理拖拽逻辑 (在绘图前处理，保证流畅)
            HandleResizeEvents();

            // 2. 绘制顶部工具栏 (固定在顶部)
            GUILayout.BeginArea(new Rect(0, 0, position.width, TOOLBAR_H));
            DrawToolbar();
            GUILayout.EndArea();

            // 计算中间内容区域的高度
            float contentHeight = position.height - TOOLBAR_H - BOTTOM_H;
            float currentY = TOOLBAR_H;

            // --- 计算三个面板的 Rect ---
            // 确保宽度不越界 (关键：防止把右侧挤没)
            float maxPreviewW = position.width - bindingListWidth - MIN_WIDTH - (SPLITTER_W * 2);
            previewWidth = Mathf.Clamp(previewWidth, MIN_WIDTH, maxPreviewW);

            float maxListW = position.width - previewWidth - MIN_WIDTH - (SPLITTER_W * 2);
            bindingListWidth = Mathf.Clamp(bindingListWidth, MIN_WIDTH, maxListW);

            // A. 左侧预览区 Rect
            Rect rectPreview = new Rect(0, currentY, previewWidth, contentHeight);

            // Splitter 1
            Rect rectSplit1 = new Rect(rectPreview.xMax, currentY, SPLITTER_W, contentHeight);

            // B. 中间列表 Rect
            Rect rectList = new Rect(rectSplit1.xMax, currentY, bindingListWidth, contentHeight);

            // Splitter 2
            Rect rectSplit2 = new Rect(rectList.xMax, currentY, SPLITTER_W, contentHeight);

            // C. 右侧树 Rect (占据剩余所有空间)
            Rect rectTree = new Rect(rectSplit2.xMax, currentY, position.width - rectSplit2.xMax, contentHeight);

            bindingViewHeight = rectList.height;
            prefabViewHeight = rectTree.height;

            // --- 3. 绘制区域内容 (使用 BeginArea 隔离) ---

            // 绘制左侧
            GUILayout.BeginArea(rectPreview);
            DrawPreviewArea(rectPreview.width, rectPreview.height);
            GUILayout.EndArea();

            // 绘制中间
            GUILayout.BeginArea(rectList);
            DrawBindingList(); // 内部有 ScrollView
            GUILayout.EndArea();

            // 绘制右侧
            GUILayout.BeginArea(rectTree);
            DrawPrefabHierarchy(); // 内部有 ScrollView
            GUILayout.EndArea();

            // --- 4. 绘制分割线视觉效果 ---
            DrawSplitter(rectSplit1);
            DrawSplitter(rectSplit2);

            // --- 5. 绘制底部栏 ---
            GUILayout.BeginArea(new Rect(0, position.height - BOTTOM_H, position.width, BOTTOM_H));
            DrawBottomBar();
            GUILayout.EndArea();
        }

        // 处理拖拽事件
        private void HandleResizeEvents()
        {
            // 重算 Splitter 的感应区域 (不依赖绘制，直接算坐标)
            float contentH = position.height - TOOLBAR_H - BOTTOM_H;
            Rect split1Rect = new Rect(previewWidth, TOOLBAR_H, SPLITTER_W, contentH);
            Rect split2Rect = new Rect(previewWidth + SPLITTER_W + bindingListWidth, TOOLBAR_H, SPLITTER_W, contentH);

            // 添加鼠标样式
            EditorGUIUtility.AddCursorRect(split1Rect, MouseCursor.ResizeHorizontal);
            EditorGUIUtility.AddCursorRect(split2Rect, MouseCursor.ResizeHorizontal);

            Event e = Event.current;

            if (e.type == EventType.MouseDown)
            {
                if (split1Rect.Contains(e.mousePosition)) isResizingPreview = true;
                if (split2Rect.Contains(e.mousePosition)) isResizingList = true;
            }

            if (isResizingPreview)
            {
                previewWidth += e.delta.x;
                Repaint();
            }
            if (isResizingList)
            {
                bindingListWidth += e.delta.x;
                Repaint();
            }

            if (e.type == EventType.MouseUp)
            {
                isResizingPreview = false;
                isResizingList = false;
            }
        }

        // 绘制分割线外观
        private void DrawSplitter(Rect rect)
        {
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f)); // 深色底
                EditorGUI.DrawRect(new Rect(rect.x + 1, rect.y, 1, rect.height), new Color(0.3f, 0.3f, 0.3f)); // 亮线
            }
        }

        // =================================================================================
        // 内容绘制方法 (已适配 BeginArea)
        // =================================================================================

        private void DrawPreviewArea(float w, float h)
        {
            // 背景
            EditorGUI.DrawRect(new Rect(0, 0, w, h), new Color(0.18f, 0.18f, 0.18f));

            GUI.Label(new Rect(5, 5, 200, 20), "视图操作: 滚轮缩放 / 中键平移", EditorStyles.miniLabel);

            // 交互区域 Rect (本地坐标)
            DrawLegend();
            Rect localRect = new Rect(0, 0, w, h);

            if (cachedPsdData != null)
            {
                HandlePreviewInput(localRect);

                // 裁剪
                GUI.BeginGroup(localRect);
                if (Event.current.type == EventType.Repaint)
                {
                    DrawCanvasContent(localRect);
                }
                GUI.EndGroup();
            }
            else
            {
                GUI.Label(new Rect(0, h / 2 - 10, w, 20), "请先加载 PSD 数据", EditorStyles.centeredGreyMiniLabel);
            }

            // Zoom 信息
            DrawStatusPanel(w);
            EditorGUI.LabelField(new Rect(5, h - 20, 100, 20), $"Zoom: {previewZoom:P0}", EditorStyles.miniLabel);
        }

        private void DrawCanvasContent(Rect viewRect)
        {
            float canvasW = cachedPsdData.width;
            float canvasH = cachedPsdData.height;
            // 计算中心点 (相对于 BeginArea 的局部坐标)
            Vector2 offset = new Vector2(viewRect.width / 2, viewRect.height / 2) + previewPan;

            Rect canvasRect = new Rect(offset.x - (canvasW / 2) * previewZoom, offset.y - (canvasH / 2) * previewZoom, canvasW * previewZoom, canvasH * previewZoom);
            EditorGUI.DrawRect(canvasRect, new Color(0.12f, 0.12f, 0.12f, 1f));
            DrawOutline(canvasRect, Color.gray);

            for (int i = 0; i < bindings.Count; i++)
            {
                DrawSingleLayerPreview(bindings[i], offset, canvasW, canvasH);
            }

            if (selectedPrefabNode != null && TryGetPrefabRect(selectedPrefabNode, offset, out var selectedPrefabRect))
            {
                bool isBound = bindings.Any(b => b.unityNode == selectedPrefabNode);
                if (!isBound)
                {
                    DrawOutline(selectedPrefabRect, PrefabOutlineColor, 1.5f);
                }
                DrawOutline(selectedPrefabRect, Color.cyan, 2f);
            }
        }

        private void DrawSingleLayerPreview(BindingPairViewModel bind, Vector2 offset, float canvasW, float canvasH)
        {
            Rect psdRect = GetPsdRect(bind.psdItem, offset, canvasW, canvasH);

            if (textureCache.TryGetValue(bind.psdItem.pngName, out Texture2D tex))
            {
                bool isSelected = (bind == selectedBinding) || (bind.unityNode != null && bind.unityNode == selectedPrefabNode);
                GUI.color = new Color(1, 1, 1, isSelected ? 1f : 0.4f);
                GUI.DrawTexture(psdRect, tex, ScaleMode.StretchToFill);
                GUI.color = Color.white;
            }
            else
            {
                DrawOutline(psdRect, new Color(1, 1, 1, 0.1f));
            }

            DrawOutline(psdRect, PsdOutlineColor, 1f);

            if (bind.unityNode != null && TryGetPrefabRect(bind.unityNode, offset, out var prefabRect))
            {
                DrawOutline(prefabRect, PrefabOutlineColor, 1.5f);

                if (bind.unityNode == selectedPrefabNode)
                {
                    DrawOutline(prefabRect, Color.cyan, 2f);
                }
            }

            if (bind == selectedBinding) DrawOutline(psdRect, Color.cyan, 2f);
        }

        // --- 中间列表 ---
        private void DrawBindingList()
        {
            // 顶部条
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("PSD 绑定列表", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("智能匹配", EditorStyles.toolbarButton)) RunAutoMatch();
            EditorGUILayout.EndHorizontal();

            // 滚动列表
            float scrollViewHeight = Mathf.Max(0f, bindingViewHeight - 24f);
            scrollPosBinding = EditorGUILayout.BeginScrollView(scrollPosBinding);

            if (bindings.Count == 0) GUILayout.Label("暂无数据", EditorStyles.centeredGreyMiniLabel);

            for (int i = 0; i < bindings.Count; i++)
            {
                var bind = bindings[i];
                float rowHeight = 40f;
                Rect rowRect = EditorGUILayout.BeginVertical(GUILayout.Height(rowHeight));

                if (pendingScrollToBinding && bind == selectedBinding)
                {
                    float yMin = rowRect.y;
                    float yMax = rowRect.y + rowRect.height;
                    if (yMin < scrollPosBinding.y) scrollPosBinding.y = yMin;
                    else if (yMax > scrollPosBinding.y + scrollViewHeight) scrollPosBinding.y = Mathf.Max(0f, yMax - scrollViewHeight);
                    pendingScrollToBinding = false;
                    Repaint();
                }

                if (bind == selectedBinding) EditorGUI.DrawRect(rowRect, new Color(0.2f, 0.5f, 0.8f, 0.5f));
                else if (i % 2 == 0) EditorGUI.DrawRect(rowRect, new Color(0, 0, 0, 0.1f));

                if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition))
                {
                    selectedBinding = bind;
                    selectedPrefabNode = bind.unityNode;
                    pendingScrollToBinding = true;
                    pendingScrollToPrefab = selectedPrefabNode != null;
                    if (pendingScrollToPrefab) ExpandToPrefabNode(selectedPrefabNode);
                    Repaint();
                }

                EditorGUILayout.BeginHorizontal();

                // 缩略图
                Texture2D tex = GetTexture(bind.psdItem);
                Rect iconRect = GUILayoutUtility.GetRect(40, 36, GUILayout.Width(40), GUILayout.Height(36));
                if (tex != null) GUI.DrawTexture(new Rect(iconRect.x + 4, iconRect.y + 2, 32, 32), tex, ScaleMode.ScaleToFit);
                else GUI.Label(new Rect(iconRect.x + 8, iconRect.y + 8, 24, 24), EditorGUIUtility.IconContent("Image Icon"));

                // 信息
                EditorGUILayout.BeginVertical();
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                if (bind.isIdMatched) GUILayout.Label(EditorGUIUtility.IconContent("LockIcon"), GUILayout.Width(14), GUILayout.Height(14));
                GUILayout.Label(bind.psdItem.pngName, EditorStyles.boldLabel, GUILayout.Height(18));
                EditorGUILayout.EndHorizontal();

                Transform old = bind.unityNode;
                bind.unityNode = (Transform)EditorGUILayout.ObjectField(bind.unityNode, typeof(Transform), true, GUILayout.Height(16));
                if (bind.unityNode != old) { bind.isConfirmed = bind.unityNode != null; UpdatePrefabStatus(); }
                EditorGUILayout.EndVertical();

                // 状态
                EditorGUILayout.BeginVertical(GUILayout.Width(25));
                GUILayout.Space(8);
                if (bind.unityNode != null)
                {
                    if (bind.isConfirmed) GUILayout.Label("√", EditorStyles.boldLabel);
                    else if (GUILayout.Button("?", EditorStyles.miniButton)) bind.isConfirmed = true;
                }
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }

        // --- 右侧树 ---
        private void DrawPrefabHierarchy()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Prefab 结构", EditorStyles.boldLabel);
            if (GUILayout.Button("刷新", EditorStyles.toolbarButton, GUILayout.Width(40))) RefreshPrefabHierarchy();
            EditorGUILayout.EndHorizontal();

            float scrollViewHeight = Mathf.Max(0f, prefabViewHeight - 20f);
            scrollPosPrefab = EditorGUILayout.BeginScrollView(scrollPosPrefab);

            if (prefabNodes.Count == 0)
            {
                GUILayout.Label("Please assign Target Root", EditorStyles.centeredGreyMiniLabel);
            }

            foreach (var node in prefabNodes)
            {
                if (!IsPrefabNodeVisible(node.transform)) continue;
                Rect rowRect = EditorGUILayout.BeginHorizontal(GUILayout.Height(20));

                bool hasChildren = node.transform != null && node.transform.childCount > 0;
                bool expanded = IsPrefabExpanded(node.transform);

                if (pendingScrollToPrefab && node.transform == selectedPrefabNode)
                {
                    float yMin = rowRect.y;
                    float yMax = rowRect.y + rowRect.height;
                    if (yMin < scrollPosPrefab.y) scrollPosPrefab.y = yMin;
                    else if (yMax > scrollPosPrefab.y + scrollViewHeight) scrollPosPrefab.y = Mathf.Max(0f, yMax - scrollViewHeight);
                    pendingScrollToPrefab = false;
                    Repaint();
                }

                if (node.transform == selectedPrefabNode) EditorGUI.DrawRect(rowRect, new Color(0.2f, 0.5f, 0.8f, 0.5f));

                if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition))
                {
                    selectedPrefabNode = node.transform;
                    selectedBinding = bindings.FirstOrDefault(b => b.unityNode == node.transform);
                    pendingScrollToPrefab = true;
                    pendingScrollToBinding = selectedBinding != null;
                    Repaint();
                }

                if (Event.current.type == EventType.MouseDrag && rowRect.Contains(Event.current.mousePosition))
                {
                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.objectReferences = new UnityObject[] { node.transform.gameObject };
                    DragAndDrop.StartDrag("Drag Prefab Node");
                    Event.current.Use();
                }

                int indent = node.depth * 14 + 4;
                Rect foldRect = new Rect(rowRect.x + indent, rowRect.y + 2, 12, 16);
                if (hasChildren)
                {
                    bool newExpanded = EditorGUI.Foldout(foldRect, expanded, GUIContent.none);
                    if (newExpanded != expanded) SetPrefabExpanded(node.transform, newExpanded);
                }
                GUILayout.Space(indent + 12);
                GUIContent icon = EditorGUIUtility.ObjectContent(node.transform.gameObject, typeof(Transform));
                GUILayout.Label(icon.image, GUILayout.Width(16), GUILayout.Height(16));

                GUI.color = node.isBound ? new Color(1, 1, 1, 0.5f) : Color.white;
                GUILayout.Label(node.transform.name, GUILayout.Height(20));
                GUI.color = Color.white;

                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }

        // =================================================================================
        // 顶部 & 底部栏 (使用 GUILayout)
        // =================================================================================
        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar, GUILayout.Height(TOOLBAR_H)); // Fixed height
            GUILayout.Label("PSD Data", GUILayout.Width(50));
            psdDataFile = EditorGUILayout.ObjectField(psdDataFile, typeof(UnityObject), false, GUILayout.Width(150));
            GUILayout.Space(10);
            GUILayout.Label("Target Root", GUILayout.Width(70));
            targetRoot = (GameObject)EditorGUILayout.ObjectField(targetRoot, typeof(GameObject), true, GUILayout.Width(150));
            GUILayout.Space(10);
            GUILayout.Label("Config:", GUILayout.Width(40));
            config = (PSDImportConfig)EditorGUILayout.ObjectField(config, typeof(PSDImportConfig), false, GUILayout.Width(120));
            if (GUILayout.Button("Load / Refresh", EditorStyles.toolbarButton, GUILayout.Width(100))) LoadData();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawBottomBar()
        {
            GUILayout.BeginHorizontal("box", GUILayout.Height(BOTTOM_H));
            GUILayout.FlexibleSpace();
            GUI.backgroundColor = new Color(0.4f, 0.9f, 0.4f);
            GUI.enabled = IsValidPsdDataFile() && targetRoot != null;
            if (GUILayout.Button("Apply All (Save)", GUILayout.Height(24), GUILayout.Width(200))) ApplyBindings();
            GUI.enabled = true;
            GUI.backgroundColor = Color.white;
            GUILayout.EndHorizontal();
        }
        // =================================================================================
        // 数据逻辑 (保持不变)
        // =================================================================================
        private void LoadData()
        {
            if (!IsValidPsdDataFile()) return;
            string path = AssetDatabase.GetAssetPath(psdDataFile);
            cachedPsdData = PSDLoader.ReadJson(path);
            if (cachedPsdData == null) return;

            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path) + "_Binding.asset";
            string assetPath = Path.Combine(dir, name);
            bindingAsset = AssetDatabase.LoadAssetAtPath<PSDBindingData>(assetPath);
            if (bindingAsset == null) { bindingAsset = CreateInstance<PSDBindingData>(); AssetDatabase.CreateAsset(bindingAsset, assetPath); }
            bindingAsset.BuildCache();

            bindings.Clear();
            textureCache.Clear();
            if (cachedPsdData.listPngData != null)
            {
                foreach (var item in cachedPsdData.listPngData)
                {
                    LoadTextureToCache(item);
                    bindings.Add(new BindingPairViewModel() { psdItem = item, unityNode = null, score = 0, isConfirmed = false, statusInfo = "等待匹配", isIdMatched = false, depth = 0 });
                }
            }
            RefreshPrefabHierarchy();
            if (targetRoot != null) RunAutoMatch();
            Repaint();
        }
        private void RefreshPrefabHierarchy()
        {
            prefabNodes.Clear();
            if (targetRoot == null) return;
            AddNodeRecursive(targetRoot.transform, 0);
            UpdatePrefabStatus();
        }
        private void AddNodeRecursive(Transform t, int depth)
        {
            prefabNodes.Add(new PrefabNodeViewModel { transform = t, depth = depth, isBound = false });
            foreach (Transform child in t) AddNodeRecursive(child, depth + 1);
        }
        private void UpdatePrefabStatus()
        {
            HashSet<Transform> boundSet = new HashSet<Transform>();
            foreach (var b in bindings) if (b.unityNode != null) boundSet.Add(b.unityNode);
            foreach (var node in prefabNodes) node.isBound = boundSet.Contains(node.transform);
        }
        private bool IsPrefabExpanded(Transform t)
        {
            if (t == null) return true;
            int id = t.GetInstanceID();
            bool expanded;
            if (!prefabFoldout.TryGetValue(id, out expanded))
            {
                expanded = true;
                prefabFoldout[id] = expanded;
            }
            return expanded;
        }

        private void SetPrefabExpanded(Transform t, bool expanded)
        {
            if (t == null) return;
            prefabFoldout[t.GetInstanceID()] = expanded;
        }

        private bool IsPrefabNodeVisible(Transform t)
        {
            if (t == null) return false;
            if (targetRoot == null) return true;
            var current = t.parent;
            while (current != null && current != targetRoot.transform)
            {
                if (!IsPrefabExpanded(current)) return false;
                current = current.parent;
            }
            return true;
        }

        private void ExpandToPrefabNode(Transform t)
        {
            if (t == null) return;
            var current = t.parent;
            while (current != null)
            {
                SetPrefabExpanded(current, true);
                if (targetRoot != null && current == targetRoot.transform) break;
                current = current.parent;
            }
        }
        private void LoadTextureToCache(PicData item)
        {
            if (item.uiType == "Text") return;
            string group = item.groupName == "root/" ? "/" : item.groupName;
            string folder = cachedPsdData.psdAssetsFolder.Replace("\\", "/");
            string pngpath = $"{folder}{group}{item.pngName}.png".Replace("//", "/");
            if (File.Exists(pngpath))
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(pngpath);
                if (tex != null && !textureCache.ContainsKey(item.pngName)) textureCache.Add(item.pngName, tex);
            }
        }

        // =================================================================================
        // 匹配与应用（委托 Service）
        // =================================================================================
        private void RunAutoMatch()
        {
            VisualBindingRestoreService.RunAutoMatch(bindings, targetRoot, cachedPsdData, bindingAsset, config);
            UpdatePrefabStatus();
        }

        private void ApplyBindings()
        {
            if (targetRoot == null || cachedPsdData == null) return;

            string psdPath = psdDataFile != null ? AssetDatabase.GetAssetPath(psdDataFile) : null;
            int count = VisualBindingRestoreService.ApplyBindings(bindings, targetRoot, cachedPsdData, bindingAsset, config, psdPath);
            ShowNotification(new GUIContent($"应用成功: {count} 个节点"));
            UpdatePrefabStatus();

            bool autoFitGroups = EditorUtility.DisplayDialog("绑定完成",
                "绑定已应用。\n是否自动调整 [空节点/组节点] 的位置？\n\n这会将所有空父节点移动到其子节点的中心，解决坐标偏移问题，利于分辨率适配。",
                "调整 (推荐)", "跳过");

            if (autoFitGroups)
            {
                PSDGroupTool.AlignGroups(targetRoot.transform);
                Debug.Log("<color=green>[PSDTools] 组节点坐标已重置到内容中心。</color>");
            }

            ShowNotification(new GUIContent($"完成! ({count} 节点)"));
            UpdatePrefabStatus();
        }

        private bool IsValidPsdDataFile()
        {
            if (psdDataFile == null) return false;
            string path = AssetDatabase.GetAssetPath(psdDataFile);
            return !string.IsNullOrEmpty(path) && path.EndsWith(".ps.data");
        }

        // Helpers
        private void HandlePreviewInput(Rect rect)
        {
            Event e = Event.current;
            if (!rect.Contains(e.mousePosition)) return;
            if (e.type == EventType.ScrollWheel) { previewZoom *= (e.delta.y > 0 ? 0.9f : 1.1f); e.Use(); }
            if (e.type == EventType.MouseDrag && e.button == 2) { previewPan += e.delta; e.Use(); }
        }
        private Rect GetPsdRect(PicData item, Vector2 offset, float canvasW, float canvasH)
        {
            float psdLocalX = item.x - canvasW * 0.5f;
            float psdLocalY = -(item.y - canvasH * 0.5f);
            return new Rect(
                offset.x + (psdLocalX - item.width / 2) * previewZoom,
                offset.y + (psdLocalY - item.height / 2) * previewZoom,
                item.width * previewZoom,
                item.height * previewZoom
            );
        }

        private bool TryGetPrefabRect(Transform node, Vector2 offset, out Rect rect)
        {
            rect = default;
            if (node == null || targetRoot == null) return false;
            RectTransform rt = node.GetComponent<RectTransform>();
            if (rt == null) return false;
            if (!node.IsChildOf(targetRoot.transform)) return false;

            Vector3 local = targetRoot.transform.InverseTransformPoint(rt.position);
            rect = new Rect(
                offset.x + (local.x - rt.rect.width / 2) * previewZoom,
                offset.y + (-local.y - rt.rect.height / 2) * previewZoom,
                rt.rect.width * previewZoom,
                rt.rect.height * previewZoom
            );
            return true;
        }

        private void DrawLegend()
        {
            const float size = 10f;
            float x = 6f;
            float y = 26f;
            DrawLegendItem(new Rect(x, y, size, size), PsdOutlineColor, "PSD");
            x += 46f;
            DrawLegendItem(new Rect(x, y, size, size), PrefabOutlineColor, "Prefab");
        }

        private void DrawLegendItem(Rect rect, Color color, string label)
        {
            EditorGUI.DrawRect(rect, color);
            GUI.Label(new Rect(rect.x + rect.width + 4, rect.y - 2, 60, 16), label, EditorStyles.miniLabel);
        }

        private void DrawStatusPanel(float viewWidth)
        {
            const float panelW = 320f;
            const float panelH = 76f;
            float x = Mathf.Max(6f, viewWidth - panelW - 6f);
            float y = 6f;
            Rect panelRect = new Rect(x, y, panelW, panelH);

            EditorGUI.DrawRect(panelRect, new Color(0f, 0f, 0f, 0.35f));
            GUI.Box(panelRect, GUIContent.none);

            float lineX = panelRect.x + 8f;
            float lineY = panelRect.y + 6f;
            float lineW = panelRect.width - 16f;

            GUI.Label(new Rect(lineX, lineY, lineW, 16f), "Status", EditorStyles.boldLabel);
            lineY += 18f;

            int total = bindings != null ? bindings.Count : 0;
            int matched = bindings != null ? bindings.Count(b => b != null && b.unityNode != null) : 0;
            int confirmed = bindings != null ? bindings.Count(b => b != null && b.isConfirmed) : 0;

            GUI.Label(new Rect(lineX, lineY, lineW, 16f), $"Bindings: {matched}/{total}", EditorStyles.miniLabel);
            lineY += 16f;
            GUI.Label(new Rect(lineX, lineY, lineW, 16f), $"Confirmed: {confirmed}", EditorStyles.miniLabel);
            lineY += 16f;
            GUI.Label(new Rect(lineX, lineY, lineW, 16f), "ML status hidden (temporarily disabled)", EditorStyles.miniLabel);
        }

        private void DrawOutline(Rect r, Color c, float w = 1)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, w), c);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - w, r.width, w), c);
            EditorGUI.DrawRect(new Rect(r.x, r.y, w, r.height), c);
            EditorGUI.DrawRect(new Rect(r.xMax - w, r.y, w, r.height), c);
        }
        private Texture2D GetTexture(PicData item)
        {
            if (string.IsNullOrEmpty(item.pngName)) return null;
            return textureCache.TryGetValue(item.pngName, out var t) ? t : null;
        }
    }
}
