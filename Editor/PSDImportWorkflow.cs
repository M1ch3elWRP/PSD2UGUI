using System;
using UnityEditor;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace PSDImporter
{
    public static class PSDImportWorkflow
    {
        private const string PsDataExtension = ".ps.data";

        public static PSDImportConfig FindDefaultConfigAsset()
        {
            string[] guids = AssetDatabase.FindAssets("t:PSDImportConfig");
            if (guids == null || guids.Length == 0)
            {
                return null;
            }

            string assetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            return AssetDatabase.LoadAssetAtPath<PSDImportConfig>(assetPath);
        }

        public static bool IsValidPsdDataAsset(UnityObject psdDataAsset)
        {
            if (psdDataAsset == null)
            {
                return false;
            }

            string path = AssetDatabase.GetAssetPath(psdDataAsset);
            return !string.IsNullOrEmpty(path) &&
                   path.EndsWith(PsDataExtension, StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryCreate(UnityObject psdDataAsset, PSDImportConfig config, out string message)
        {
            if (!TryLoadData(psdDataAsset, out PSDData psdData, out message))
            {
                return false;
            }

            PSDImportConfig runtimeConfig = ResolveRuntimeConfig(config, out PSDImportConfig tempConfig);
            try
            {
                PSDCreateor.CreateUGUI_GenerateMode(psdData, runtimeConfig);
                message = $"创建完成，共处理 {psdData.listPngData.Count} 个图层。";
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                message = $"创建失败: {ex.Message}";
                return false;
            }
            finally
            {
                ReleaseTempConfig(tempConfig);
            }
        }

        public static bool TryRestore(UnityObject psdDataAsset, GameObject targetRoot, PSDImportConfig config, out string message)
        {
            if (targetRoot == null)
            {
                message = "请先指定 Target Root。";
                return false;
            }

            if (!TryLoadData(psdDataAsset, out PSDData psdData, out message))
            {
                return false;
            }

            PSDImportConfig runtimeConfig = ResolveRuntimeConfig(config, out PSDImportConfig tempConfig);
            try
            {
                Undo.RegisterFullObjectHierarchyUndo(targetRoot, "PSD Restore UI");
                PSDCreateor.CreateUGUI_SyncMode(psdData, targetRoot.transform, runtimeConfig);
                message = $"还原完成，共处理 {psdData.listPngData.Count} 个图层。";
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                message = $"还原失败: {ex.Message}";
                return false;
            }
            finally
            {
                ReleaseTempConfig(tempConfig);
            }
        }

        private static bool TryLoadData(UnityObject psdDataAsset, out PSDData psdData, out string message)
        {
            psdData = null;

            if (psdDataAsset == null)
            {
                message = "请先选择 .ps.data 文件。";
                return false;
            }

            string path = AssetDatabase.GetAssetPath(psdDataAsset);
            if (string.IsNullOrEmpty(path) ||
                !path.EndsWith(PsDataExtension, StringComparison.OrdinalIgnoreCase))
            {
                message = "文件格式不正确，请选择 .ps.data 文件。";
                return false;
            }

            psdData = PSDLoader.ReadJson(path);
            if (psdData == null)
            {
                message = "读取 .ps.data 失败，请检查文件内容。";
                return false;
            }

            message = string.Empty;
            return true;
        }

        private static PSDImportConfig ResolveRuntimeConfig(PSDImportConfig config, out PSDImportConfig tempConfig)
        {
            tempConfig = null;
            if (config != null)
            {
                return config;
            }

            tempConfig = ScriptableObject.CreateInstance<PSDImportConfig>();
            return tempConfig;
        }

        private static void ReleaseTempConfig(PSDImportConfig tempConfig)
        {
            if (tempConfig != null)
            {
                UnityObject.DestroyImmediate(tempConfig);
            }
        }
    }
}
