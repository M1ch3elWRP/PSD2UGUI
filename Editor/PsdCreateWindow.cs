using UnityEditor;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace PSDImporter
{
    public class PsdCreateWindow : EditorWindow
    {
        private UnityObject psdDataFile;
        private PSDImportConfig config;

        [MenuItem("PSD2NGUI/Create UI From PSD", priority = 0)]
        public static void ShowWindow()
        {
            PSDEditorWindowUtility.ShowCenteredUtility<PsdCreateWindow>(
                "PSD Create",
                new Vector2(420f, 280f),
                new Vector2(380f, 240f));
            Debug.Log("[PSDTools] Opened PSD Create window.");
        }

        private void OnEnable()
        {
            try
            {
                if (config == null)
                {
                    config = PSDImportWorkflow.FindDefaultConfigAsset();
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private void OnGUI()
        {
            GUILayout.Space(10);
            GUILayout.Label("Create UI From PSD", EditorStyles.boldLabel);
            GUILayout.Label("窗口仅负责参数输入与触发，创建逻辑由 PSDImportWorkflow 执行。", EditorStyles.wordWrappedMiniLabel);
            GUILayout.Space(6);

            using (new EditorGUILayout.VerticalScope("box"))
            {
                GUILayout.Label("数据源", EditorStyles.miniBoldLabel);
                psdDataFile = EditorGUILayout.ObjectField("PSD Data (*.ps.data)", psdDataFile, typeof(UnityObject), false);
                config = (PSDImportConfig)EditorGUILayout.ObjectField("Import Config", config, typeof(PSDImportConfig), false);

                if (config == null)
                {
                    EditorGUILayout.HelpBox("未指定配置，将使用默认配置参数。", MessageType.Info);
                }
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = PSDImportWorkflow.IsValidPsdDataAsset(psdDataFile);
                if (GUILayout.Button("Create UI", GUILayout.Height(30)))
                {
                    ExecuteCreate();
                }
                GUI.enabled = true;

                if (GUILayout.Button("Open Restore", GUILayout.Height(30), GUILayout.Width(120)))
                {
                    VisualBindingWindow.ShowWindow();
                }
            }
        }

        private void ExecuteCreate()
        {
            bool success = PSDImportWorkflow.TryCreate(psdDataFile, config, out string message, out GameObject root);
            if (success)
            {
                ShowNotification(new GUIContent(message));

                bool autoFitGroups = EditorUtility.DisplayDialog("创建完成",
                    "创建已应用。\n是否自动调整 [空节点/组节点] 的位置？\n\n这会将所有空父节点移动到其子节点的中心，解决坐标偏移问题，利于分辨率适配。",
                    "调整 (推荐)", "跳过");

                if (autoFitGroups && root != null)
                {
                    PSDGroupTool.AlignGroups(root.transform);
                    Debug.Log("<color=green>[PSDTools] 组节点坐标已重置到内容中心。</color>");
                }
                return;
            }

            EditorUtility.DisplayDialog("Create UI", message, "OK");
        }
    }
}
