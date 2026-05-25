using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using TZ.Framework.UGUI;

namespace PSDImporter
{
    internal static class PSDStdItemTestRunner
    {
        private const string TestFolder = "Assets/_OpenCode/TZUI/PS/ItemTest";

        [MenuItem("PSDTools/Debug/Run StdItemTest Create Smoke", priority = 202)]
        private static void RunCreateSmoke()
        {
            string testAssetPath = FindFirstPsDataInFolder(TestFolder);
            if (string.IsNullOrEmpty(testAssetPath))
            {
                Debug.LogError("[StdItemTest] Failed to find .ps.data in " + TestFolder);
                return;
            }

            PSDData psdData = PSDLoader.ReadJson(testAssetPath);
            if (psdData == null)
            {
                Debug.LogError("[StdItemTest] Failed to read test asset: " + testAssetPath);
                return;
            }

            PSDImportConfig config = PSDImportWorkflow.FindDefaultConfigAsset();
            RectTransform rootRect = PSDCreateor.CreateUGUI_GenerateMode(psdData, config);
            GameObject root = rootRect != null ? rootRect.gameObject : null;
            if (root == null)
            {
                Debug.LogError("[StdItemTest] Create returned null root.");
                return;
            }

            Selection.activeGameObject = root;

            UIItemPool[] pools = root.GetComponentsInChildren<UIItemPool>(true);
            UIPrefabLink[] prefabLinks = root.GetComponentsInChildren<UIPrefabLink>(true);
            List<GameObject> runtimeItems = new List<GameObject>();
            for (int i = 0; i < prefabLinks.Length; i++)
            {
                UIPrefabLink link = prefabLinks[i];
                if (link == null)
                {
                    continue;
                }

                if (!link.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (link.GetComponentInParent<UIItemPool>() != null)
                {
                    continue;
                }

                runtimeItems.Add(link.gameObject);
            }

            Debug.Log($"[StdItemTest] Create success. root={root.name} test={Path.GetFileName(testAssetPath)} pools={pools.Length} items={runtimeItems.Count}");
            for (int i = 0; i < pools.Length; i++)
            {
                UIItemPool pool = pools[i];
                string panelPath = pool.panel != null ? GetTransformPath(pool.panel.transform) : "<null>";
                string prefabPath = pool.itemPrefab != null ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(pool.itemPrefab.gameObject) : "<null>";
                string templatePath = pool.itemPrefab != null ? GetTransformPath(pool.itemPrefab.transform) : "<null>";
                Debug.Log($"[StdItemTest] Pool[{i}] path={GetTransformPath(pool.transform)} panel={panelPath} prefab={prefabPath} template={templatePath}");
            }

            for (int i = 0; i < runtimeItems.Count; i++)
            {
                GameObject itemGo = runtimeItems[i];
                string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(itemGo);
                string source = string.IsNullOrEmpty(assetPath) ? "scene-template-clone" : assetPath;
                Debug.Log($"[StdItemTest] Item[{i}] path={GetTransformPath(itemGo.transform)} source={source} active={itemGo.activeInHierarchy}");
            }
        }

        private static string FindFirstPsDataInFolder(string folder)
        {
            string[] guids = AssetDatabase.FindAssets("t:DefaultAsset", new[] { folder });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (path.EndsWith(".ps.data"))
                {
                    return path;
                }
            }

            return null;
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
            {
                return "<null>";
            }

            List<string> names = new List<string>();
            Transform cursor = transform;
            while (cursor != null)
            {
                names.Add(cursor.name);
                cursor = cursor.parent;
            }

            names.Reverse();
            return string.Join("/", names);
        }
    }
}
