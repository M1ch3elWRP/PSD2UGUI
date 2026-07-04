using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PSDImporter;
using UnityEditor;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace UnityMCP
{
    [InitializeOnLoad]
    public static class PSDToolsMCPBridge
    {
        private const string DefaultPsdRoot = "Assets/_OpenCode/TZUI/PS";

        static PSDToolsMCPBridge()
        {
            UnityHttpServer.RegisterMethod("psd/listData", ListData, true, true);
            UnityHttpServer.RegisterMethod("psd/inspectData", InspectData, true, true);
            UnityHttpServer.RegisterMethod("psd/createPrefab", CreatePrefab, true, true);

            UnityHttpServer.RegisterMethod("prefab/list", ListPrefabs, true, true);
            UnityHttpServer.RegisterMethod("prefab/inspect", InspectPrefab, true, true);
            UnityHttpServer.RegisterMethod("prefab/instantiate", InstantiatePrefab, true, true);
            UnityHttpServer.RegisterMethod("prefab/saveFromScene", SavePrefabFromScene, true, true);

            UnityHttpServer.RegisterMethod("ugui/find", FindUguiObjects, true, true);
            UnityHttpServer.RegisterMethod("ugui/rectTransform", ManageRectTransform, true, true);
        }

        private static object ListData(JObject request)
        {
            string rootPath = NormalizeAssetPath(GetString(request, "rootPath", DefaultPsdRoot));
            bool recursive = GetBool(request, "recursive", true);
            bool readSummary = GetBool(request, "readSummary", false);
            int limit = Mathf.Max(1, GetInt(request, "limit", 200));

            string fullRoot = ToAbsolutePath(rootPath);
            if (!Directory.Exists(fullRoot))
            {
                throw new DirectoryNotFoundException("PSD root folder not found: " + rootPath);
            }

            SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            string[] files = Directory.GetFiles(fullRoot, "*.ps.data", searchOption)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(limit)
                .ToArray();

            List<object> entries = new List<object>();
            foreach (string file in files)
            {
                string assetPath = ToAssetPath(file);
                FileInfo info = new FileInfo(file);
                object summary = readSummary ? BuildPsdDataInfo(assetPath, false) : null;
                entries.Add(new
                {
                    path = assetPath,
                    name = Path.GetFileName(file),
                    folder = Path.GetDirectoryName(assetPath).Replace("\\", "/"),
                    guid = AssetDatabase.AssetPathToGUID(assetPath),
                    sizeBytes = info.Length,
                    lastWriteTime = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    summary = summary
                });
            }

            return new
            {
                rootPath = rootPath,
                recursive = recursive,
                returned = entries.Count,
                entries = entries
            };
        }

        private static object InspectData(JObject request)
        {
            string psdDataPath = RequirePsdDataPath(request);
            return BuildPsdDataInfo(psdDataPath, true);
        }

        private static object CreatePrefab(JObject request)
        {
            string psdDataPath = RequirePsdDataPath(request);
            string importConfigPath = GetString(request, "importConfigPath", null);
            string savePrefabPath = NormalizeOptionalAssetPath(GetString(request, "savePrefabPath", null));
            bool replaceExistingPrefab = GetBool(request, "replaceExistingPrefab", false);
            bool alignGroups = GetBool(request, "alignGroups", false);
            bool selectCreatedRoot = GetBool(request, "selectCreatedRoot", true);

            UnityObject psdDataAsset = AssetDatabase.LoadAssetAtPath<UnityObject>(psdDataPath);
            if (psdDataAsset == null)
            {
                throw new FileNotFoundException("PSD data asset not found: " + psdDataPath);
            }

            PSDImportConfig config = ResolveImportConfig(importConfigPath);
            GameObject root;
            string message;
            bool ok = PSDImportWorkflow.TryCreate(psdDataAsset, config, out message, out root);
            if (!ok || root == null)
            {
                throw new InvalidOperationException(string.IsNullOrEmpty(message) ? "PSD create failed." : message);
            }

            if (alignGroups)
            {
                PSDGroupTool.AlignGroups(root.transform);
            }

            string prefabGuid = "";
            if (!string.IsNullOrEmpty(savePrefabPath))
            {
                if (!savePrefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    savePrefabPath += ".prefab";
                }

                if (!replaceExistingPrefab && AssetDatabase.LoadAssetAtPath<GameObject>(savePrefabPath) != null)
                {
                    throw new InvalidOperationException("Prefab already exists: " + savePrefabPath);
                }

                EnsureAssetFolder(Path.GetDirectoryName(savePrefabPath));
                GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, savePrefabPath);
                if (saved == null)
                {
                    throw new InvalidOperationException("Failed to save prefab: " + savePrefabPath);
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                prefabGuid = AssetDatabase.AssetPathToGUID(savePrefabPath);
            }

            if (selectCreatedRoot)
            {
                Selection.activeGameObject = root;
                EditorGUIUtility.PingObject(root);
            }

            return new
            {
                success = true,
                message = message,
                psdDataPath = psdDataPath,
                rootName = root.name,
                rootPath = GetHierarchyPath(root),
                alignGroups = alignGroups,
                prefabPath = savePrefabPath,
                prefabGuid = prefabGuid,
                scenePath = root.scene.IsValid() ? root.scene.path : ""
            };
        }

        private static object ListPrefabs(JObject request)
        {
            string rootPath = NormalizeAssetPath(GetString(request, "rootPath", "Assets"));
            bool recursive = GetBool(request, "recursive", true);
            int limit = Mathf.Max(1, GetInt(request, "limit", 200));
            string nameContains = GetString(request, "nameContains", null);

            if (!AssetDatabase.IsValidFolder(rootPath))
            {
                throw new DirectoryNotFoundException("Prefab root folder not found: " + rootPath);
            }

            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { rootPath });
            List<object> entries = new List<object>();
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!recursive && Path.GetDirectoryName(path).Replace("\\", "/") != rootPath.TrimEnd('/'))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(nameContains) &&
                    Path.GetFileNameWithoutExtension(path).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                {
                    continue;
                }

                entries.Add(new
                {
                    path = path,
                    name = prefab.name,
                    guid = guid,
                    folder = Path.GetDirectoryName(path).Replace("\\", "/")
                });

                if (entries.Count >= limit)
                {
                    break;
                }
            }

            return new
            {
                rootPath = rootPath,
                recursive = recursive,
                returned = entries.Count,
                entries = entries
            };
        }

        private static object InspectPrefab(JObject request)
        {
            string prefabPath = RequirePrefabPath(request);
            bool includeComponents = GetBool(request, "includeComponents", true);
            int maxDepth = Mathf.Max(0, GetInt(request, "maxDepth", 6));

            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                return new
                {
                    path = prefabPath,
                    guid = AssetDatabase.AssetPathToGUID(prefabPath),
                    hierarchy = BuildGameObjectTree(root, 0, maxDepth, includeComponents)
                };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static object InstantiatePrefab(JObject request)
        {
            string prefabPath = RequirePrefabPath(request);
            string parentPath = GetString(request, "parentPath", null);
            string instanceName = GetString(request, "name", null);
            bool active = GetBool(request, "active", true);
            bool selectInstance = GetBool(request, "selectInstance", true);

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                throw new FileNotFoundException("Prefab not found: " + prefabPath);
            }

            Transform parent = null;
            if (!string.IsNullOrEmpty(parentPath))
            {
                GameObject parentGo = FindSceneGameObject(parentPath);
                if (parentGo == null)
                {
                    throw new InvalidOperationException("Parent GameObject not found: " + parentPath);
                }

                parent = parentGo.transform;
            }

            GameObject instance = PrefabUtility.InstantiatePrefab(prefab, parent) as GameObject;
            if (instance == null)
            {
                throw new InvalidOperationException("Failed to instantiate prefab: " + prefabPath);
            }

            if (!string.IsNullOrEmpty(instanceName))
            {
                instance.name = instanceName;
            }

            instance.SetActive(active);

            Vector3 vector3;
            if (TryReadVector3(request["localPosition"], out vector3))
            {
                instance.transform.localPosition = vector3;
            }

            if (TryReadVector3(request["localScale"], out vector3))
            {
                instance.transform.localScale = vector3;
            }

            if (TryReadVector3(request["localRotationEuler"], out vector3))
            {
                instance.transform.localEulerAngles = vector3;
            }

            if (selectInstance)
            {
                Selection.activeGameObject = instance;
                EditorGUIUtility.PingObject(instance);
            }

            Undo.RegisterCreatedObjectUndo(instance, "MCP Instantiate Prefab");
            return new
            {
                prefabPath = prefabPath,
                instanceName = instance.name,
                instancePath = GetHierarchyPath(instance),
                parentPath = parent != null ? GetHierarchyPath(parent.gameObject) : "",
                scenePath = instance.scene.IsValid() ? instance.scene.path : ""
            };
        }

        private static object SavePrefabFromScene(JObject request)
        {
            string gameObjectPath = GetString(request, "gameObjectPath", null);
            string prefabPath = NormalizeOptionalAssetPath(GetString(request, "prefabPath", null));
            bool replaceExisting = GetBool(request, "replaceExisting", false);
            bool selectSavedPrefab = GetBool(request, "selectSavedPrefab", true);
            bool dryRun = GetBool(request, "dryRun", false);

            if (string.IsNullOrEmpty(gameObjectPath))
            {
                throw new ArgumentException("gameObjectPath is required");
            }

            if (string.IsNullOrEmpty(prefabPath))
            {
                throw new ArgumentException("prefabPath is required");
            }

            if (!prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                prefabPath += ".prefab";
            }

            GameObject go = FindSceneGameObject(gameObjectPath);
            if (go == null)
            {
                throw new InvalidOperationException("Scene GameObject not found: " + gameObjectPath);
            }

            bool exists = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null;
            if (exists && !replaceExisting)
            {
                throw new InvalidOperationException("Prefab already exists: " + prefabPath);
            }

            if (dryRun)
            {
                return new
                {
                    dryRun = true,
                    gameObjectPath = GetHierarchyPath(go),
                    prefabPath = prefabPath,
                    replaceExisting = replaceExisting,
                    exists = exists
                };
            }

            EnsureAssetFolder(Path.GetDirectoryName(prefabPath));
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            if (saved == null)
            {
                throw new InvalidOperationException("Failed to save prefab: " + prefabPath);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (selectSavedPrefab)
            {
                Selection.activeObject = saved;
                EditorGUIUtility.PingObject(saved);
            }

            return new
            {
                gameObjectPath = GetHierarchyPath(go),
                prefabPath = prefabPath,
                guid = AssetDatabase.AssetPathToGUID(prefabPath),
                replaced = exists
            };
        }

        private static object FindUguiObjects(JObject request)
        {
            string rootPath = GetString(request, "rootPath", null);
            string nameContains = GetString(request, "nameContains", null);
            bool includeInactive = GetBool(request, "includeInactive", true);
            int maxResults = Mathf.Max(1, GetInt(request, "maxResults", 200));
            HashSet<string> componentTypes = ReadStringSet(request["componentTypes"]);

            GameObject root = string.IsNullOrEmpty(rootPath) ? null : FindSceneGameObject(rootPath);
            if (!string.IsNullOrEmpty(rootPath) && root == null)
            {
                throw new InvalidOperationException("Root GameObject not found: " + rootPath);
            }

            IEnumerable<RectTransform> rects = root != null
                ? root.GetComponentsInChildren<RectTransform>(includeInactive)
                : Resources.FindObjectsOfTypeAll<RectTransform>().Where(IsSceneObject);

            List<object> results = new List<object>();
            foreach (RectTransform rect in rects)
            {
                GameObject go = rect.gameObject;
                if (!includeInactive && !go.activeInHierarchy)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(nameContains) &&
                    go.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                string[] components = GetComponentTypeNames(go).ToArray();
                if (componentTypes.Count > 0 && !components.Any(componentTypes.Contains))
                {
                    continue;
                }

                results.Add(new
                {
                    path = GetHierarchyPath(go),
                    name = go.name,
                    activeSelf = go.activeSelf,
                    activeInHierarchy = go.activeInHierarchy,
                    components = components,
                    rectTransform = BuildRectTransformInfo(rect)
                });

                if (results.Count >= maxResults)
                {
                    break;
                }
            }

            return new
            {
                rootPath = rootPath,
                returned = results.Count,
                results = results
            };
        }

        private static object ManageRectTransform(JObject request)
        {
            string action = GetString(request, "action", "get");
            string gameObjectPath = GetString(request, "gameObjectPath", null);
            if (string.IsNullOrEmpty(gameObjectPath))
            {
                throw new ArgumentException("gameObjectPath is required");
            }

            GameObject go = FindSceneGameObject(gameObjectPath);
            if (go == null)
            {
                throw new InvalidOperationException("GameObject not found: " + gameObjectPath);
            }

            RectTransform rect = go.GetComponent<RectTransform>();
            if (rect == null)
            {
                throw new InvalidOperationException("GameObject has no RectTransform: " + gameObjectPath);
            }

            if (string.Equals(action, "get", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    path = GetHierarchyPath(go),
                    rectTransform = BuildRectTransformInfo(rect)
                };
            }

            if (!string.Equals(action, "set", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Unsupported rectTransform action: " + action);
            }

            bool dryRun = GetBool(request, "dryRun", false);
            object before = BuildRectTransformInfo(rect);
            if (!dryRun)
            {
                Undo.RecordObject(rect, "MCP Set RectTransform");
                Vector2 vector2;
                Vector3 vector3;

                if (TryReadVector2(request["anchorMin"], out vector2)) rect.anchorMin = vector2;
                if (TryReadVector2(request["anchorMax"], out vector2)) rect.anchorMax = vector2;
                if (TryReadVector2(request["pivot"], out vector2)) rect.pivot = vector2;
                if (TryReadVector2(request["anchoredPosition"], out vector2)) rect.anchoredPosition = vector2;
                if (TryReadVector2(request["sizeDelta"], out vector2)) rect.sizeDelta = vector2;
                if (TryReadVector2(request["offsetMin"], out vector2)) rect.offsetMin = vector2;
                if (TryReadVector2(request["offsetMax"], out vector2)) rect.offsetMax = vector2;
                if (TryReadVector3(request["localScale"], out vector3)) rect.localScale = vector3;
                if (TryReadVector3(request["localRotationEuler"], out vector3)) rect.localEulerAngles = vector3;

                EditorUtility.SetDirty(rect);
            }

            return new
            {
                path = GetHierarchyPath(go),
                dryRun = dryRun,
                before = before,
                after = BuildRectTransformInfo(rect)
            };
        }

        private static object BuildPsdDataInfo(string psdDataPath, bool includeLayerSamples)
        {
            PSDData data = PSDLoader.ReadJson(psdDataPath);
            if (data == null)
            {
                throw new InvalidOperationException("Failed to read PSD data: " + psdDataPath);
            }

            int textCount = data.listPngData.Count(item => item.isText);
            int buttonCount = data.listPngData.Count(item => string.Equals(item.uiType, "Button", StringComparison.OrdinalIgnoreCase));
            int imageCount = data.listPngData.Count(item => string.Equals(item.uiType, "Image", StringComparison.OrdinalIgnoreCase));
            int scrollContentAliasCount = data.listPngData.Count(item => item.isScrollContentAlias);
            int skeletonCount = data.skeleton != null ? data.skeleton.Count : 0;

            IEnumerable<object> layerSamples = Enumerable.Empty<object>();
            if (includeLayerSamples)
            {
                layerSamples = data.listPngData.Take(40).Select(item => new
                {
                    id = item.id,
                    name = item.pngName,
                    cleanName = item.cleanName,
                    uiType = item.uiType,
                    groupName = item.groupName,
                    isText = item.isText,
                    layoutType = item.layoutType,
                    rect = new { x = item.x, y = item.y, width = item.width, height = item.height }
                }).ToArray();
            }

            return new
            {
                path = psdDataPath,
                guid = AssetDatabase.AssetPathToGUID(psdDataPath),
                canvas = new { width = data.width, height = data.height },
                psdAssetsFolder = data.psdAssetsFolder,
                templateFound = data.hasTemplatePng,
                templatePngPath = data.templatePngPath,
                layerCount = data.listPngData.Count,
                textLayerCount = textCount,
                imageLayerCount = imageCount,
                buttonLayerCount = buttonCount,
                scrollContentAliasCount = scrollContentAliasCount,
                skeletonCount = skeletonCount,
                layerSamples = layerSamples
            };
        }

        private static PSDImportConfig ResolveImportConfig(string importConfigPath)
        {
            string path = NormalizeOptionalAssetPath(importConfigPath);
            if (!string.IsNullOrEmpty(path))
            {
                PSDImportConfig config = AssetDatabase.LoadAssetAtPath<PSDImportConfig>(path);
                if (config == null)
                {
                    throw new FileNotFoundException("PSDImportConfig not found: " + path);
                }

                return config;
            }

            return PSDImportWorkflow.FindDefaultConfigAsset();
        }

        private static string RequirePsdDataPath(JObject request)
        {
            string path = NormalizeOptionalAssetPath(GetString(request, "psdDataPath", null));
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("psdDataPath is required");
            }

            if (!path.EndsWith(".ps.data", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("psdDataPath must end with .ps.data: " + path);
            }

            string fullPath = ToAbsolutePath(path);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("PSD data file not found: " + path);
            }

            return path;
        }

        private static void EnsureAssetFolder(string folderPath)
        {
            folderPath = NormalizeAssetPath(folderPath);
            if (string.IsNullOrEmpty(folderPath) || folderPath == "Assets" || AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            string parent = Path.GetDirectoryName(folderPath).Replace("\\", "/");
            string name = Path.GetFileName(folderPath);
            EnsureAssetFolder(parent);
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                AssetDatabase.CreateFolder(parent, name);
            }
        }

        private static string RequirePrefabPath(JObject request)
        {
            return RequirePrefabPath(GetString(request, "prefabPath", null));
        }

        private static string RequirePrefabPath(string prefabPath)
        {
            string path = NormalizeOptionalAssetPath(prefabPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("prefabPath is required");
            }

            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("prefabPath must end with .prefab: " + path);
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
            {
                throw new FileNotFoundException("Prefab not found: " + path);
            }

            return path;
        }

        private static object BuildGameObjectTree(GameObject go, int depth, int maxDepth, bool includeComponents)
        {
            RectTransform rect = go.GetComponent<RectTransform>();
            List<object> children = new List<object>();
            bool truncated = depth >= maxDepth && go.transform.childCount > 0;
            if (!truncated)
            {
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    children.Add(BuildGameObjectTree(go.transform.GetChild(i).gameObject, depth + 1, maxDepth, includeComponents));
                }
            }

            return new
            {
                name = go.name,
                path = GetHierarchyPath(go),
                activeSelf = go.activeSelf,
                layer = LayerMask.LayerToName(go.layer),
                childCount = go.transform.childCount,
                truncated = truncated,
                components = includeComponents ? GetComponentTypeNames(go).ToArray() : null,
                rectTransform = rect != null ? BuildRectTransformInfo(rect) : null,
                children = children
            };
        }

        private static GameObject FindSceneGameObject(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            string normalized = path.Trim('/');
            GameObject direct = GameObject.Find(normalized);
            if (direct != null)
            {
                return direct;
            }

            foreach (Transform transform in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (!IsSceneObject(transform))
                {
                    continue;
                }

                if (string.Equals(GetHierarchyPath(transform.gameObject), normalized, StringComparison.Ordinal))
                {
                    return transform.gameObject;
                }
            }

            return null;
        }

        private static bool IsSceneObject(Component component)
        {
            return component != null &&
                   component.gameObject.scene.IsValid() &&
                   !EditorUtility.IsPersistent(component.gameObject);
        }

        private static IEnumerable<string> GetComponentTypeNames(GameObject go)
        {
            foreach (Component component in go.GetComponents<Component>())
            {
                yield return component == null ? "<MissingScript>" : component.GetType().Name;
            }
        }

        private static object BuildRectTransformInfo(RectTransform rect)
        {
            return new
            {
                anchorMin = Vec2(rect.anchorMin),
                anchorMax = Vec2(rect.anchorMax),
                pivot = Vec2(rect.pivot),
                anchoredPosition = Vec2(rect.anchoredPosition),
                sizeDelta = Vec2(rect.sizeDelta),
                offsetMin = Vec2(rect.offsetMin),
                offsetMax = Vec2(rect.offsetMax),
                localPosition = Vec3(rect.localPosition),
                localRotationEuler = Vec3(rect.localEulerAngles),
                localScale = Vec3(rect.localScale),
                rect = new
                {
                    x = rect.rect.x,
                    y = rect.rect.y,
                    width = rect.rect.width,
                    height = rect.rect.height
                }
            };
        }

        private static HashSet<string> ReadStringSet(JToken token)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (token == null || token.Type == JTokenType.Null)
            {
                return result;
            }

            if (token.Type == JTokenType.Array)
            {
                foreach (JToken item in token)
                {
                    string value = item.ToString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        result.Add(value);
                    }
                }
            }
            else
            {
                string value = token.ToString();
                if (!string.IsNullOrEmpty(value))
                {
                    result.Add(value);
                }
            }

            return result;
        }

        private static bool TryReadVector2(JToken token, out Vector2 value)
        {
            value = default(Vector2);
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            if (token.Type == JTokenType.Array)
            {
                JArray array = (JArray)token;
                if (array.Count < 2)
                {
                    return false;
                }

                value = new Vector2(array[0].Value<float>(), array[1].Value<float>());
                return true;
            }

            value = new Vector2(token["x"]?.Value<float>() ?? 0f, token["y"]?.Value<float>() ?? 0f);
            return true;
        }

        private static bool TryReadVector3(JToken token, out Vector3 value)
        {
            value = default(Vector3);
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            if (token.Type == JTokenType.Array)
            {
                JArray array = (JArray)token;
                if (array.Count < 3)
                {
                    return false;
                }

                value = new Vector3(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>());
                return true;
            }

            value = new Vector3(
                token["x"]?.Value<float>() ?? 0f,
                token["y"]?.Value<float>() ?? 0f,
                token["z"]?.Value<float>() ?? 0f);
            return true;
        }

        private static object Vec2(Vector2 value)
        {
            return new { x = value.x, y = value.y };
        }

        private static object Vec3(Vector3 value)
        {
            return new { x = value.x, y = value.y, z = value.z };
        }

        private static string GetHierarchyPath(GameObject go)
        {
            if (go == null)
            {
                return "";
            }

            List<string> names = new List<string>();
            Transform current = go.transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static string GetString(JObject request, string key, string fallback)
        {
            JToken token = request[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }

            string value = token.ToString();
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        private static bool GetBool(JObject request, string key, bool fallback)
        {
            JToken token = request[key];
            return token == null || token.Type == JTokenType.Null ? fallback : token.Value<bool>();
        }

        private static int GetInt(JObject request, string key, int fallback)
        {
            JToken token = request[key];
            return token == null || token.Type == JTokenType.Null ? fallback : token.Value<int>();
        }

        private static string NormalizeOptionalAssetPath(string path)
        {
            return string.IsNullOrEmpty(path) ? null : NormalizeAssetPath(path);
        }

        private static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            string normalized = path.Replace("\\", "/");
            string dataPath = Application.dataPath.Replace("\\", "/");
            string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace("\\", "/");

            if (Path.IsPathRooted(normalized))
            {
                if (normalized.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
                {
                    return "Assets" + normalized.Substring(dataPath.Length);
                }

                if (normalized.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return normalized.Substring(projectRoot.Length).TrimStart('/');
                }
            }

            return normalized.TrimStart('/');
        }

        private static string ToAbsolutePath(string assetPath)
        {
            string normalized = NormalizeAssetPath(assetPath);
            if (Path.IsPathRooted(normalized))
            {
                return normalized.Replace("\\", "/");
            }

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, normalized).Replace("\\", "/");
        }

        private static string ToAssetPath(string fullPath)
        {
            string normalized = fullPath.Replace("\\", "/");
            string dataPath = Application.dataPath.Replace("\\", "/");
            if (normalized.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
            {
                return "Assets" + normalized.Substring(dataPath.Length);
            }

            string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace("\\", "/");
            if (normalized.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return normalized.Substring(projectRoot.Length).TrimStart('/');
            }

            return normalized;
        }
    }
}
