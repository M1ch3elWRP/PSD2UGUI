using UnityEngine;
using UnityEditor;
using PSDImporter;

public class PSDImporterWindow : EditorWindow
{
    private Object psdDataFile;
    private GameObject targetRoot;
    private PSDImportConfig config; // 【新增】配置槽位

    // Deprecated: use VisualBindingWindow instead.
    //[MenuItem("PSDTools/Open Importer Window", priority = 0)]
    public static void ShowWindow()
    {
        var window = GetWindow<PSDImporterWindow>("PSD Importer");
        window.minSize = new Vector2(300, 400); // 稍微加高一点
        window.Show();
    }

    private void OnGUI()
    {
        GUILayout.Space(10);
        GUILayout.Label("PSD to UGUI 工具箱", EditorStyles.boldLabel);
        GUILayout.Space(5);

        // 1. 数据源
        EditorGUILayout.BeginVertical("box");
        GUILayout.Label("1. 数据与配置", EditorStyles.miniLabel);
        psdDataFile = EditorGUILayout.ObjectField("PSD Data (*.data)", psdDataFile, typeof(Object), false);

        // 【新增】配置选择框
        config = (PSDImportConfig)EditorGUILayout.ObjectField("匹配算法配置", config, typeof(PSDImportConfig), false);
        if (config == null)
        {
            EditorGUILayout.HelpBox("未指定配置，将使用默认参数。", MessageType.Info);
        }
        EditorGUILayout.EndVertical();

        GUILayout.Space(10);

        // 2. 模式 A
        EditorGUILayout.BeginVertical("helpbox");
        GUILayout.Label("模式 A: 全新生成 (Create)", EditorStyles.boldLabel);
        if (GUILayout.Button("创建新界面", GUILayout.Height(30)))
        {
            if (CheckDataFile())
            {
                var data = PSDLoader.ReadJson(AssetDatabase.GetAssetPath(psdDataFile));
                if (data != null)
                {
                    // 传入配置
                    PSDCreateor.CreateUGUI_GenerateMode(data, GetConfig());
                    ShowNotification(new GUIContent("创建成功!"));
                }
            }
        }
        EditorGUILayout.EndVertical();

        GUILayout.Space(10);

        // 3. 模式 B
        EditorGUILayout.BeginVertical("helpbox");
        GUILayout.Label("模式 B: 同步注入 (Sync)", EditorStyles.boldLabel);
        targetRoot = (GameObject)EditorGUILayout.ObjectField("目标根节点", targetRoot, typeof(GameObject), true);

        if (GUILayout.Button("同步 (智能匹配)", GUILayout.Height(30)))
        {
            if (CheckDataFile() && targetRoot != null)
            {
                var data = PSDLoader.ReadJson(AssetDatabase.GetAssetPath(psdDataFile));
                if (data != null)
                {
                    Undo.RegisterFullObjectHierarchyUndo(targetRoot, "PSD Sync UI");
                    // 传入配置
                    PSDCreateor.CreateUGUI_SyncMode(data, targetRoot.transform, GetConfig());
                    ShowNotification(new GUIContent("同步完成!"));
                }
            }
        }
        EditorGUILayout.EndVertical();
    }

    private PSDImportConfig GetConfig()
    {
        if (config != null) return config;
        // 如果没拖配置，临时生成一个默认的
        var temp = ScriptableObject.CreateInstance<PSDImportConfig>();
        return temp;
    }

    private bool CheckDataFile()
    {
        if (psdDataFile == null) return false;
        return AssetDatabase.GetAssetPath(psdDataFile).EndsWith(".ps.data");
    }
}
