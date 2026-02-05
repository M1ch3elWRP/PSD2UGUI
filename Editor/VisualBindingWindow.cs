using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

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
        private Object psdDataFile;
        private GameObject targetRoot;
        private PSDImportConfig config;
        private enum ImportMode { Create, Restore }
        private ImportMode importMode = ImportMode.Restore;

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

        // --- 【布局核心参数】 ---
        private float previewWidth = 450f;
        private float bindingListWidth = 400f;

        private static readonly Color PsdOutlineColor = new Color(0.25f, 0.65f, 1f, 0.7f);
        private static readonly Color PrefabOutlineColor = new Color(1f, 0.7f, 0.2f, 0.75f);

        private PSDMatchModel statusMlModel;
        private string statusMlModelPath;
        private double statusMlModelNextCheck;

        // 拖拽状态
        private bool isResizingPreview = false;
        private bool isResizingList = false;

        // 常量配置
        private const float MIN_WIDTH = 200f;   // 任何面板的最小宽度
        private const float SPLITTER_W = 4f;    // 分割线宽度
        private const float TOOLBAR_H = 20f;    // 顶部高度
        private const float BOTTOM_H = 30f;     // 底部高度

        [MenuItem("PSDTools/Visual Binding Tool (可视化绑定)", priority = 1)]
        public static void ShowWindow()
        {
            var win = GetWindow<VisualBindingWindow>("UI Visual Binder");
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
            DrawStatusPanel(w);
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
            scrollPosBinding = EditorGUILayout.BeginScrollView(scrollPosBinding);

            if (bindings.Count == 0) GUILayout.Label("暂无数据", EditorStyles.centeredGreyMiniLabel);

            for (int i = 0; i < bindings.Count; i++)
            {
                var bind = bindings[i];
                float rowHeight = 40f;
                Rect rowRect = EditorGUILayout.BeginVertical(GUILayout.Height(rowHeight));

                if (bind == selectedBinding) EditorGUI.DrawRect(rowRect, new Color(0.2f, 0.5f, 0.8f, 0.5f));
                else if (i % 2 == 0) EditorGUI.DrawRect(rowRect, new Color(0, 0, 0, 0.1f));

                if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition))
                {
                    selectedBinding = bind;
                    selectedPrefabNode = bind.unityNode;
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

            scrollPosPrefab = EditorGUILayout.BeginScrollView(scrollPosPrefab);

            if (prefabNodes.Count == 0)
            {
                string msg = importMode == ImportMode.Create ? "Create mode does not need Target Root" : "Please assign Target Root";
                GUILayout.Label(msg, EditorStyles.centeredGreyMiniLabel);
            }

            foreach (var node in prefabNodes)
            {
                Rect rowRect = EditorGUILayout.BeginHorizontal(GUILayout.Height(20));

                if (node.transform == selectedPrefabNode) EditorGUI.DrawRect(rowRect, new Color(0.2f, 0.5f, 0.8f, 0.5f));

                if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition))
                {
                    selectedPrefabNode = node.transform;
                    selectedBinding = bindings.FirstOrDefault(b => b.unityNode == node.transform);
                    Repaint();
                }

                if (Event.current.type == EventType.MouseDrag && rowRect.Contains(Event.current.mousePosition))
                {
                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.objectReferences = new Object[] { node.transform.gameObject };
                    DragAndDrop.StartDrag("Drag Prefab Node");
                    Event.current.Use();
                }

                GUILayout.Space(node.depth * 14 + 4);
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
            GUILayout.Label("Mode", GUILayout.Width(30));
            var newMode = (ImportMode)EditorGUILayout.EnumPopup(importMode, GUILayout.Width(90));
            if (newMode != importMode)
            {
                importMode = newMode;
                OnModeChanged();
            }
            GUILayout.Space(6);
            GUILayout.Label("PSD Data", GUILayout.Width(50));
            psdDataFile = EditorGUILayout.ObjectField(psdDataFile, typeof(Object), false, GUILayout.Width(150));
            if (importMode == ImportMode.Restore)
            {
                GUILayout.Space(10);
                GUILayout.Label("Target Root", GUILayout.Width(70));
                targetRoot = (GameObject)EditorGUILayout.ObjectField(targetRoot, typeof(GameObject), true, GUILayout.Width(150));
            }
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
            if (importMode == ImportMode.Create)
            {
                GUI.backgroundColor = new Color(0.35f, 0.7f, 1f);
                GUI.enabled = IsValidPsdDataFile();
                if (GUILayout.Button("Create UI", GUILayout.Height(24), GUILayout.Width(160))) CreateNewUI();
            }
            else
            {
                GUI.backgroundColor = new Color(0.4f, 0.9f, 0.4f);
                GUI.enabled = IsValidPsdDataFile() && targetRoot != null;
                if (GUILayout.Button("Apply All (Save)", GUILayout.Height(24), GUILayout.Width(200))) ApplyBindings();
            }
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

            if (importMode == ImportMode.Restore)
            {
                string dir = Path.GetDirectoryName(path);
                string name = Path.GetFileNameWithoutExtension(path) + "_Binding.asset";
                string assetPath = Path.Combine(dir, name);
                bindingAsset = AssetDatabase.LoadAssetAtPath<PSDBindingData>(assetPath);
                if (bindingAsset == null) { bindingAsset = CreateInstance<PSDBindingData>(); AssetDatabase.CreateAsset(bindingAsset, assetPath); }
                bindingAsset.BuildCache();
            }
            else
            {
                bindingAsset = null;
            }

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
            if (importMode == ImportMode.Restore)
            {
                RefreshPrefabHierarchy();
                if (targetRoot != null) RunAutoMatch();
            }
            else
            {
                prefabNodes.Clear();
                selectedPrefabNode = null;
            }
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
        // 匹配与应用
        // =================================================================================
        private struct ScoreBreakdown
        {
            public float distance;
            public float diffW;
            public float diffH;
            public float scorePos;
            public float scoreSize;
            public float scoreType;
            public float weightedPos;
            public float weightedSize;
            public float weightedType;
            public float total;
            public bool passThresholds;
            public float mlProb;
            public bool mlUsed;
        }

        private class MatchCandidate
        {
            public BindingPairViewModel bind;
            public Transform node;
            public float score;
            public string reason;
            public bool isPerfect;
            public ScoreBreakdown breakdown;
        }
        private void RunAutoMatch()
        {
            if (targetRoot == null || bindings.Count == 0) return;
            var matchConfig = config ?? ScriptableObject.CreateInstance<PSDImportConfig>();
            bool logDetail = matchConfig != null && matchConfig.showDetailedLog;
            bool forceCandidateLog = matchConfig != null && matchConfig.forceCandidateLog;
            bool logCandidatesInLoop = logDetail && !forceCandidateLog;
            bool useMlScore = matchConfig != null && matchConfig.useMlScore;
            PSDMatchModel mlModel = null;
            if (useMlScore)
            {
                mlModel = PSDMatchAutoLearn.TryLoadModel(matchConfig);
                if (mlModel == null)
                {
                    useMlScore = false;
                    if (logDetail) Debug.LogWarning("[Match] ML model not found. Fallback to manual weights.");
                }
            }
            float perfectThreshold = useMlScore ? 80f : 150f;
            HashSet<Transform> occupiedNodes = new HashSet<Transform>();
            HashSet<BindingPairViewModel> matchedBindings = new HashSet<BindingPairViewModel>();
            if (logDetail)
            {
                Debug.Log($"[Match] Config maxDist={matchConfig.maxDistanceError:F1}, maxSizeDiff={matchConfig.maxSizeDiff:F1}, weightPos={matchConfig.weightPosition:F2}, weightSize={matchConfig.weightSize:F2}, weightType={matchConfig.weightType:F2}, skipInactive={matchConfig.skipInactiveMatch}, forceCandidateLog={forceCandidateLog}, useMlScore={useMlScore}");
            }
            foreach (var bind in bindings)
            {
                if (bind.isConfirmed && bind.unityNode != null) { occupiedNodes.Add(bind.unityNode); matchedBindings.Add(bind); }
                else { bind.unityNode = null; bind.score = 0; bind.statusInfo = "Waiting for match"; bind.isIdMatched = false; }
            }

            var pendingBinds = new List<BindingPairViewModel>();
            foreach (var bind in bindings)
            {
                if (matchedBindings.Contains(bind)) continue;
                if (bindingAsset != null)
                {
                    GameObject savedGo = bindingAsset.GetBindTarget(bind.psdItem.id);
                    if (savedGo != null && savedGo.transform.IsChildOf(targetRoot.transform) && !occupiedNodes.Contains(savedGo.transform))
                    {
                        bind.unityNode = savedGo.transform;
                        bind.score = 9999f;
                        bind.statusInfo = "ID history binding";
                        bind.isIdMatched = true;
                        bind.isConfirmed = true;
                        matchedBindings.Add(bind);
                        occupiedNodes.Add(savedGo.transform);
                        if (logDetail)
                        {
                            Debug.Log($"[Match] Saved binding for {bind.psdItem.pngName} -> {GetTransformPath(savedGo.transform)} score=9999 (ID)");
                        }
                        continue;
                    }
                }
                pendingBinds.Add(bind);
            }

            var allNodes = targetRoot.GetComponentsInChildren<RectTransform>(true);
            var nodeList = new List<RectTransform>();
            var nodeListForLog = forceCandidateLog ? new List<RectTransform>() : nodeList;
            int skippedRoot = 0;
            int skippedOccupied = 0;
            int skippedInactive = 0;
            int skippedRootLog = 0;
            int skippedInactiveLog = 0;
            int skippedOccupiedLog = 0;
            foreach (var node in allNodes)
            {
                if (node == targetRoot.transform) { skippedRoot++; skippedRootLog++; continue; }
                if (matchConfig.skipInactiveMatch && !node.gameObject.activeInHierarchy) { skippedInactive++; skippedInactiveLog++; continue; }

                bool isOccupied = occupiedNodes.Contains(node.transform);
                if (isOccupied) skippedOccupied++; else nodeList.Add(node);
                if (forceCandidateLog)
                {
                    if (isOccupied) skippedOccupiedLog++;
                    nodeListForLog.Add(node);
                }
            }

            if (logDetail)
            {
                Debug.Log($"[Match] Candidate nodes={nodeList.Count}, skipped(root:{skippedRoot}, occupied:{skippedOccupied}, inactive:{skippedInactive})");
                if (forceCandidateLog)
                {
                    Debug.Log($"[Match] Candidate nodes(for log)={nodeListForLog.Count}, skipped(root:{skippedRootLog}, occupied:{skippedOccupiedLog}, inactive:{skippedInactiveLog})");
                }
            }

            if (logDetail && forceCandidateLog)
            {
                foreach (var bind in bindings)
                {
                    LogTopCandidatesForBind(bind, nodeListForLog, matchConfig, useMlScore, mlModel, skippedRootLog, skippedInactiveLog, skippedOccupiedLog);
                }
            }

            int itemCount = pendingBinds.Count;
            int nodeCount = nodeList.Count;
            if (itemCount == 0 || nodeCount == 0)
            {
                foreach (var bind in pendingBinds) bind.statusInfo = "No suitable node found";
                UpdatePrefabStatus();
                return;
            }

            float[,] scoreMatrix = new float[itemCount, nodeCount];
            bool[,] validMatrix = new bool[itemCount, nodeCount];
            float maxScore = 0f;

            for (int i = 0; i < itemCount; i++)
            {
                var bind = pendingBinds[i];
                float localX = bind.psdItem.x - cachedPsdData.width * 0.5f;
                float localY = bind.psdItem.y - cachedPsdData.height * 0.5f;
                Vector3 targetWorldPos = targetRoot.transform.TransformPoint(new Vector3(localX, localY, 0));
                List<MatchCandidate> localCandidates = logCandidatesInLoop ? new List<MatchCandidate>() : null;
                int skippedType = 0;

                for (int j = 0; j < nodeCount; j++)
                {
                    var node = nodeList[j];
                    if (!IsTypeMatch(node, bind.psdItem.uiType)) { skippedType++; continue; }

                    ScoreBreakdown breakdown;
                    float score = GetMatchScore(bind.psdItem, node, targetWorldPos, matchConfig, useMlScore, mlModel, out breakdown);
                    if (score > 1f)
                    {
                        validMatrix[i, j] = true;
                        scoreMatrix[i, j] = score;
                        if (score > maxScore) maxScore = score;
                        if (logCandidatesInLoop)
                        {
                            var candidate = new MatchCandidate { bind = bind, node = node, score = score, isPerfect = score > perfectThreshold, breakdown = breakdown };
                            localCandidates.Add(candidate);
                        }
                    }
                }

                if (logCandidatesInLoop)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"[Match] Item {bind.psdItem.pngName} (id:{bind.psdItem.id}, type:{bind.psdItem.uiType}) pos=({bind.psdItem.x:F1},{bind.psdItem.y:F1}) size=({bind.psdItem.width:F1},{bind.psdItem.height:F1}) targetWorld=({targetWorldPos.x:F1},{targetWorldPos.y:F1}) candidates={localCandidates.Count} skipped(type:{skippedType}, inactive:{skippedInactive}, occupied:{skippedOccupied}, root:{skippedRoot})");
                    var top = localCandidates.OrderByDescending(c => c.score).Take(5).ToList();
                    for (int k = 0; k < top.Count; k++)
                    {
                        var cand = top[k];
                        var b = cand.breakdown;
                        var rt = cand.node as RectTransform;
                        float nodeW = rt != null ? rt.rect.width : 0f;
                        float nodeH = rt != null ? rt.rect.height : 0f;
                        string mlInfo = b.mlUsed ? $" mlScore={cand.score:F1} mlProb={b.mlProb:F3}" : string.Empty;
                        sb.AppendLine($"  #{k + 1} {GetTransformPath(cand.node)} active={cand.node.gameObject.activeInHierarchy} nodeSize=({nodeW:F1},{nodeH:F1}) dist={b.distance:F1} diff=({b.diffW:F1},{b.diffH:F1}) scorePos={b.scorePos:F1} scoreSize={b.scoreSize:F1} scoreType={b.scoreType:F0} w=({b.weightedPos:F1},{b.weightedSize:F1},{b.weightedType:F1}) total={b.total:F1}{mlInfo}");
                    }
                    Debug.Log(sb.ToString());
                }
            }

            if (maxScore <= 0f)
            {
                foreach (var bind in pendingBinds) bind.statusInfo = "No suitable node found";
                UpdatePrefabStatus();
                return;
            }

            int size = Mathf.Max(itemCount, nodeCount);
            float invalidCost = maxScore + 1000f;
            float[,] cost = new float[size, size];
            for (int i = 0; i < size; i++)
            {
                for (int j = 0; j < size; j++)
                {
                    cost[i, j] = invalidCost;
                }
            }
            for (int i = 0; i < itemCount; i++)
            {
                for (int j = 0; j < nodeCount; j++)
                {
                    if (validMatrix[i, j])
                    {
                        cost[i, j] = maxScore - scoreMatrix[i, j];
                    }
                }
            }

            int[] assignment = PSDHungarianSolver.Solve(cost);
            for (int i = 0; i < itemCount; i++)
            {
                int j = assignment[i];
                if (j < 0 || j >= nodeCount) continue;
                if (!validMatrix[i, j]) continue;
                var bind = pendingBinds[i];
                var node = nodeList[j];
                if (occupiedNodes.Contains(node.transform)) continue;
                bind.unityNode = node;
                bind.score = scoreMatrix[i, j];
                bind.statusInfo = $"Score: {bind.score:F0}";
                bind.isIdMatched = false;
                if (bind.score > perfectThreshold) bind.isConfirmed = true;
                matchedBindings.Add(bind);
                occupiedNodes.Add(node.transform);
                if (logDetail)
                {
                    ScoreBreakdown breakdown;
                    GetMatchScore(bind.psdItem, node, targetRoot.transform.TransformPoint(new Vector3(bind.psdItem.x - cachedPsdData.width * 0.5f, bind.psdItem.y - cachedPsdData.height * 0.5f, 0)), matchConfig, useMlScore, mlModel, out breakdown);
                    var rt = node as RectTransform;
                    float nodeW = rt != null ? rt.rect.width : 0f;
                    float nodeH = rt != null ? rt.rect.height : 0f;
                    string mlInfo = breakdown.mlUsed ? $" mlScore={bind.score:F1} mlProb={breakdown.mlProb:F3}" : string.Empty;
                    Debug.Log($"[Match] Result {bind.psdItem.pngName} -> {GetTransformPath(node)} score={bind.score:F1} dist={breakdown.distance:F1} diff=({breakdown.diffW:F1},{breakdown.diffH:F1}) nodeSize=({nodeW:F1},{nodeH:F1}) w=({breakdown.weightedPos:F1},{breakdown.weightedSize:F1},{breakdown.weightedType:F1}){mlInfo}");
                }
            }

            foreach (var bind in pendingBinds)
            {
                if (!matchedBindings.Contains(bind)) bind.statusInfo = "No suitable node found";
            }
            UpdatePrefabStatus();
        }

        private void LogTopCandidatesForBind(BindingPairViewModel bind, IList<RectTransform> nodes, PSDImportConfig config, bool useMlScore, PSDMatchModel mlModel, int skippedRoot, int skippedInactive, int skippedOccupied)
        {
            if (bind == null || nodes == null || config == null) return;

            float localX = bind.psdItem.x - cachedPsdData.width * 0.5f;
            float localY = bind.psdItem.y - cachedPsdData.height * 0.5f;
            Vector3 targetWorldPos = targetRoot.transform.TransformPoint(new Vector3(localX, localY, 0));

            List<MatchCandidate> localCandidates = new List<MatchCandidate>();
            int skippedType = 0;
            for (int j = 0; j < nodes.Count; j++)
            {
                var node = nodes[j];
                if (!IsTypeMatch(node, bind.psdItem.uiType)) { skippedType++; continue; }

                ScoreBreakdown breakdown;
                float score = GetMatchScore(bind.psdItem, node, targetWorldPos, config, useMlScore, mlModel, out breakdown);
                if (score > 1f)
                {
                    localCandidates.Add(new MatchCandidate { bind = bind, node = node, score = score, isPerfect = score > 150f, breakdown = breakdown });
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[Match] Item {bind.psdItem.pngName} (id:{bind.psdItem.id}, type:{bind.psdItem.uiType}) pos=({bind.psdItem.x:F1},{bind.psdItem.y:F1}) size=({bind.psdItem.width:F1},{bind.psdItem.height:F1}) targetWorld=({targetWorldPos.x:F1},{targetWorldPos.y:F1}) candidates={localCandidates.Count} skipped(type:{skippedType}, inactive:{skippedInactive}, occupied:{skippedOccupied}, root:{skippedRoot})");
            var top = localCandidates.OrderByDescending(c => c.score).Take(5).ToList();
            for (int k = 0; k < top.Count; k++)
            {
                var cand = top[k];
                var b = cand.breakdown;
                var rt = cand.node as RectTransform;
                float nodeW = rt != null ? rt.rect.width : 0f;
                float nodeH = rt != null ? rt.rect.height : 0f;
                string mlInfo = b.mlUsed ? $" mlScore={cand.score:F1} mlProb={b.mlProb:F3}" : string.Empty;
                sb.AppendLine($"  #{k + 1} {GetTransformPath(cand.node)} active={cand.node.gameObject.activeInHierarchy} nodeSize=({nodeW:F1},{nodeH:F1}) dist={b.distance:F1} diff=({b.diffW:F1},{b.diffH:F1}) scorePos={b.scorePos:F1} scoreSize={b.scoreSize:F1} scoreType={b.scoreType:F0} w=({b.weightedPos:F1},{b.weightedSize:F1},{b.weightedType:F1}) total={b.total:F1}{mlInfo}");
            }
            Debug.Log(sb.ToString());
        }

        private void ApplyBindings()
        {
            if (importMode != ImportMode.Restore) return;
            if (targetRoot == null) return;
            Undo.RegisterFullObjectHierarchyUndo(targetRoot, "Apply Visual Bindings");
            int count = 0;
            foreach (var bind in bindings)
            {
                if (bind.unityNode == null) continue;
                PSDCreateor.RefreshNode(bind.unityNode.gameObject, bind.psdItem, cachedPsdData, true, config);
                if (bind.isConfirmed) bindingAsset.SaveBinding(bind.psdItem.id, bind.psdItem.pngName, bind.unityNode.gameObject);
                count++;
            }
            EditorUtility.SetDirty(bindingAsset); AssetDatabase.SaveAssets(); ShowNotification(new GUIContent($"应用成功: {count} 个节点")); UpdatePrefabStatus();

            // =========================================================
            // 【新增】第2步：自动清洗组节点位置
            // =========================================================
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
            if (config != null && config.autoLearnEnabled)
            {
                string psdPath = psdDataFile != null ? AssetDatabase.GetAssetPath(psdDataFile) : null;
                PSDMatchAutoLearn.RecordAndMaybeTrain(psdPath, cachedPsdData, targetRoot.transform, bindings, config);
            }
        }

        private void CreateNewUI()
        {
            if (!IsValidPsdDataFile()) return;
            string path = AssetDatabase.GetAssetPath(psdDataFile);
            var data = PSDLoader.ReadJson(path);
            if (data == null) return;
            var useConfig = config != null ? config : ScriptableObject.CreateInstance<PSDImportConfig>();
            PSDCreateor.CreateUGUI_GenerateMode(data, useConfig);
            ShowNotification(new GUIContent("Created!"));
        }

        private void OnModeChanged()
        {
            if (importMode == ImportMode.Create)
            {
                targetRoot = null;
                prefabNodes.Clear();
                selectedPrefabNode = null;
            }
            Repaint();
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

        private static float CalculateMatchScoreDetailed(PicData item, RectTransform node, Vector3 targetWorldPos, PSDImportConfig config, out ScoreBreakdown breakdown)
        {
            breakdown = new ScoreBreakdown();
            if (config == null || node == null) return 0f;

            float dist = Vector3.Distance(node.position, targetWorldPos);
            float scorePos = 0f;
            if (dist < config.maxDistanceError)
            {
                scorePos = (1f - (dist / config.maxDistanceError)) * 100f;
            }

            float diffW = Mathf.Abs(node.rect.width - item.width);
            float diffH = Mathf.Abs(node.rect.height - item.height);
            float scoreSize = 0f;
            if ((diffW + diffH) < config.maxSizeDiff)
            {
                scoreSize = (1f - ((diffW + diffH) / config.maxSizeDiff)) * 100f;
            }

            float scoreType = IsTypeMatch(node, item.uiType) ? 100f : 0f;
            bool pass = scorePos > 0f || scoreSize > 0f;
            float weightedPos = scorePos * config.weightPosition;
            float weightedSize = scoreSize * config.weightSize;
            float weightedType = scoreType * config.weightType;
            float total = pass ? (weightedPos + weightedSize + weightedType) : 0f;

            breakdown.distance = dist;
            breakdown.diffW = diffW;
            breakdown.diffH = diffH;
            breakdown.scorePos = scorePos;
            breakdown.scoreSize = scoreSize;
            breakdown.scoreType = scoreType;
            breakdown.weightedPos = weightedPos;
            breakdown.weightedSize = weightedSize;
            breakdown.weightedType = weightedType;
            breakdown.total = total;
            breakdown.passThresholds = pass;

            return total;
        }

        private float GetMatchScore(PicData item, RectTransform node, Vector3 targetWorldPos, PSDImportConfig config, bool useMlScore, PSDMatchModel mlModel, out ScoreBreakdown breakdown)
        {
            if (!useMlScore || mlModel == null)
            {
                return CalculateMatchScoreDetailed(item, node, targetWorldPos, config, out breakdown);
            }

            float maxDist = mlModel.maxDistanceError > 0f ? mlModel.maxDistanceError : config.maxDistanceError;
            float maxSize = mlModel.maxSizeDiff > 0f ? mlModel.maxSizeDiff : config.maxSizeDiff;
            int maxDepth = mlModel.maxDepthDiff > 0 ? mlModel.maxDepthDiff : config.autoLearnMaxDepthDiff;

            float[] x = PSDMatchFeatureExtractor.ExtractFeatures(item, node, targetRoot.transform, cachedPsdData.width, cachedPsdData.height, maxDist, maxSize, maxDepth);
            float prob = PSDMatchML.Predict(mlModel, x);
            float score = prob * 100f;

            CalculateMatchScoreDetailed(item, node, targetWorldPos, config, out breakdown);
            breakdown.mlProb = prob;
            breakdown.mlUsed = true;
            return score;
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

        private static string GetTransformPath(Transform t)
        {
            if (t == null) return string.Empty;
            var parts = new List<string>();
            var current = t;
            while (current != null)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
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
            const float panelW = 300f;
            const float panelH = 88f;
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
            int matched = bindings != null ? bindings.Count(b => b.unityNode != null) : 0;
            int confirmed = bindings != null ? bindings.Count(b => b.isConfirmed) : 0;
            GUI.Label(new Rect(lineX, lineY, lineW, 16f), $"Items: {total}  Matched: {matched}  Confirmed: {confirmed}", EditorStyles.miniLabel);
            lineY += 16f;

            if (config != null)
            {
                GUI.Label(new Rect(lineX, lineY, lineW, 16f),
                    $"Manual W: Pos={config.weightPosition:F2} Size={config.weightSize:F2} Type={config.weightType:F2}",
                    EditorStyles.miniLabel);
            }
            else
            {
                GUI.Label(new Rect(lineX, lineY, lineW, 16f), "Manual W: n/a", EditorStyles.miniLabel);
            }
            lineY += 16f;

            PSDMatchModel model = GetStatusModel();
            if (model != null && model.weights != null && model.weights.Length >= 3)
            {
                GUI.Label(new Rect(lineX, lineY, lineW, 16f),
                    $"ML W (Dist/Size/Type): {model.weights[0]:F3}, {model.weights[1]:F3}, {model.weights[2]:F3}",
                    EditorStyles.miniLabel);
            }
            else
            {
                GUI.Label(new Rect(lineX, lineY, lineW, 16f), "ML W (Dist/Size/Type): n/a", EditorStyles.miniLabel);
            }
        }

        private PSDMatchModel GetStatusModel()
        {
            if (config == null) return null;
            if (string.IsNullOrEmpty(config.autoLearnModelPath)) return null;

            if (statusMlModelPath != config.autoLearnModelPath)
            {
                statusMlModelPath = config.autoLearnModelPath;
                statusMlModel = null;
                statusMlModelNextCheck = 0;
            }

            if (EditorApplication.timeSinceStartup >= statusMlModelNextCheck)
            {
                statusMlModel = PSDMatchAutoLearn.TryLoadModel(config);
                statusMlModelNextCheck = EditorApplication.timeSinceStartup + 1.0;
            }

            return statusMlModel;
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
