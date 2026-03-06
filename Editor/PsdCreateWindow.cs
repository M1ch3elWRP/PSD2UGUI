using UnityEditor;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace PSDImporter
{
    public class PsdCreateWindow : EditorWindow
    {
        private UnityObject psdDataFile;
        private PSDImportConfig config;

        [MenuItem("PSDTools/Create UI From PSD", priority = 0)]
        public static void ShowWindow()
        {
            var window = GetWindow<PsdCreateWindow>("PSD Create");
            window.minSize = new Vector2(380, 240);
            window.Show();
        }

        private void OnEnable()
        {
            if (config == null)
            {
                config = PSDImportWorkflow.FindDefaultConfigAsset();
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
            bool success = PSDImportWorkflow.TryCreate(psdDataFile, config, out string message);
            if (success)
            {
                ShowNotification(new GUIContent(message));
                return;
            }

            EditorUtility.DisplayDialog("Create UI", message, "OK");
        }
    }
}
