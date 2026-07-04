using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace PSDImporter
{
    public static class PSDRenderCaptureUtility
    {
        private const float CaptureCameraZ = -10000f;

        public static string CaptureToPng(GameObject targetRoot, int width, int height, string outputAssetPath)
        {
            if (targetRoot == null)
            {
                throw new ArgumentNullException("targetRoot");
            }

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("Capture size must be positive.");
            }

            outputAssetPath = NormalizeAssetPath(outputAssetPath);
            EnsureAssetFolder(Path.GetDirectoryName(outputAssetPath));
            string outputFullPath = ToAbsolutePath(outputAssetPath);

            GameObject cameraGo = null;
            GameObject clone = null;
            RenderTexture renderTexture = null;
            Texture2D texture = null;

            try
            {
                cameraGo = new GameObject("__PSDTools_CVCapture_Camera");
                cameraGo.hideFlags = HideFlags.HideAndDontSave;
                Camera camera = cameraGo.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                camera.orthographic = true;
                camera.orthographicSize = height * 0.5f;
                camera.aspect = width / (float)height;
                camera.nearClipPlane = -2f;
                camera.farClipPlane = 2f;
                camera.orthographicSize = height * 0.5f;
                camera.aspect = width / (float)height;
                camera.allowHDR = false;
                camera.allowMSAA = false;
                int uiLayer = LayerMask.NameToLayer("UI");
                if (uiLayer >= 0) camera.cullingMask = 1 << uiLayer;
                camera.transform.position = new Vector3(0f, 0f, CaptureCameraZ);
                camera.transform.rotation = Quaternion.identity;

                clone = UnityObject.Instantiate(targetRoot);
                clone.name = targetRoot.name + "_CVCaptureClone";
                clone.hideFlags = HideFlags.HideAndDontSave;
                clone.SetActive(true);
                PrepareCloneForCapture(clone, camera, width, height);

                // NGUI：通知所有 UIPanel 重排并标记父级变更，让 UISprite/UILabel/UITable 立即刷新
                var panels = clone.GetComponentsInChildren<UIPanel>(true);
                for (int i = 0; i < panels.Length; i++)
                {
                    if (panels[i] != null) panels[i].SetDirty();
                }
                NGUITools.MarkParentChanged();

                renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                renderTexture.antiAliasing = 1;
                renderTexture.Create();

                RenderTexture previousTarget = camera.targetTexture;
                RenderTexture previousActive = RenderTexture.active;
                camera.targetTexture = renderTexture;
                RenderTexture.active = renderTexture;
                camera.Render();

                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();

                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;

                File.WriteAllBytes(outputFullPath, texture.EncodeToPNG());
                AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                ConfigurePngImporter(outputAssetPath, width, height);
                return outputAssetPath;
            }
            finally
            {
                if (texture != null)
                {
                    UnityObject.DestroyImmediate(texture);
                }

                if (renderTexture != null)
                {
                    renderTexture.Release();
                    UnityObject.DestroyImmediate(renderTexture);
                }

                if (clone != null)
                {
                    UnityObject.DestroyImmediate(clone);
                }

                if (cameraGo != null)
                {
                    UnityObject.DestroyImmediate(cameraGo);
                }
            }
        }

        public static void ConfigurePngImporter(string outputAssetPath, int width = 0, int height = 0)
        {
            TextureImporter importer = AssetImporter.GetAtPath(outputAssetPath) as TextureImporter;
            if (importer == null)
            {
                return;
            }

            int maxDimension = Mathf.Max(width, height);
            if (maxDimension <= 0)
            {
                Vector2Int imageSize = ReadPngSize(outputAssetPath);
                maxDimension = Mathf.Max(imageSize.x, imageSize.y);
            }

            int desiredMaxSize = Mathf.Clamp(Mathf.NextPowerOfTwo(maxDimension), 32, 8192);
            bool dirty = false;
            if (importer.textureType != TextureImporterType.Default)
            {
                importer.textureType = TextureImporterType.Default;
                dirty = true;
            }

            if (importer.maxTextureSize < desiredMaxSize)
            {
                importer.maxTextureSize = desiredMaxSize;
                dirty = true;
            }

            if (!importer.alphaIsTransparency)
            {
                importer.alphaIsTransparency = true;
                dirty = true;
            }

            if (importer.npotScale != TextureImporterNPOTScale.None)
            {
                importer.npotScale = TextureImporterNPOTScale.None;
                dirty = true;
            }

            if (importer.mipmapEnabled)
            {
                importer.mipmapEnabled = false;
                dirty = true;
            }

            if (importer.textureCompression != TextureImporterCompression.Uncompressed)
            {
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                dirty = true;
            }

            if (dirty)
            {
                importer.SaveAndReimport();
            }
        }

        private static Vector2Int ReadPngSize(string assetPath)
        {
            string fullPath = ToAbsolutePath(assetPath);
            if (!File.Exists(fullPath))
            {
                return Vector2Int.one;
            }

            Texture2D temp = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (ImageConversion.LoadImage(temp, File.ReadAllBytes(fullPath), true))
                {
                    return new Vector2Int(temp.width, temp.height);
                }
            }
            finally
            {
                UnityObject.DestroyImmediate(temp);
            }

            return Vector2Int.one;
        }

        private static void PrepareCloneForCapture(GameObject clone, Camera camera, int width, int height)
        {
            RectTransform cloneRect = clone.GetComponent<RectTransform>();
            if (cloneRect == null)
            {
                cloneRect = clone.AddComponent<RectTransform>();
            }

            clone.transform.SetParent(null, false);
            cloneRect.anchorMin = new Vector2(0.5f, 0.5f);
            cloneRect.anchorMax = new Vector2(0.5f, 0.5f);
            cloneRect.pivot = new Vector2(0.5f, 0.5f);
            cloneRect.anchoredPosition = Vector2.zero;
            cloneRect.localPosition = Vector3.zero;
            cloneRect.localRotation = Quaternion.identity;
            cloneRect.localScale = Vector3.one;
            cloneRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            cloneRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);

            Canvas rootCanvas = clone.GetComponent<Canvas>();
            if (rootCanvas == null)
            {
                // NGUI 路径：clone 已经是 UIRoot（来自 CreateNGUI_GenerateMode）
                // 只需要确保所有 UIPanel 都能被 camera 渲染（layer=UI + cullingMask 已在 camera 上设置）
                var uiRoot = clone.GetComponent<UIRoot>();
                if (uiRoot == null)
                {
                    // 非 NGUI 兜底：clone 不是 UIRoot，加个 Canvas 让渲染管线能跑
                    rootCanvas = clone.AddComponent<Canvas>();
                    rootCanvas.renderMode = RenderMode.ScreenSpaceCamera;
                    rootCanvas.worldCamera = camera;
                    rootCanvas.planeDistance = Mathf.Abs(CaptureCameraZ);
                    rootCanvas.overrideSorting = true;
                    rootCanvas.sortingOrder = 0;
                }
            }

            // 把所有 UIPanel alpha 拉满，深度排序按现有顺序保留
            foreach (UIPanel panel in clone.GetComponentsInChildren<UIPanel>(true))
            {
                panel.alpha = 1f;
            }
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
    }
}
