using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PSDImporter
{
    public enum PSDImageReuseSourceKind
    {
        OriginalExport,
        LocalDuplicate,
        ProjectCommonExact,
        ProjectCommonTint,
        ProjectCommonSliced,
        ProjectCommonPerceptual,
        Missing,
        CurrentImage
    }

    [Serializable]
    public class PSDImageBorderLog
    {
        public float left;
        public float bottom;
        public float right;
        public float top;

        public static PSDImageBorderLog From(Vector4 border)
        {
            return new PSDImageBorderLog
            {
                left = border.x,
                bottom = border.y,
                right = border.z,
                top = border.w
            };
        }
    }

    [Serializable]
    public class PSDImageTintLog
    {
        public float r;
        public float g;
        public float b;
        public float a;

        public static PSDImageTintLog From(Color color)
        {
            return new PSDImageTintLog
            {
                r = color.r,
                g = color.g,
                b = color.b,
                a = color.a
            };
        }
    }

    [Serializable]
    public class PSDImageReuseLog
    {
        public string sourceKind;
        public string spritePath;
        public string originalExportPath;
        public string resolvedAssetPath;
        public string canonicalExportPath;
        public bool hasSlice;
        public PSDImageBorderLog sliceBorder;
        public bool hasTint;
        public PSDImageTintLog tintColor;
        public float confidence;
        public string matchReason;
        public string rejectReason;
        public string diagnostics;
    }

    public class PSDImageReuseResult
    {
        public PSDImageReuseSourceKind sourceKind = PSDImageReuseSourceKind.OriginalExport;
        public Sprite sprite;
        public string spritePath;
        public string originalExportPath;
        public string resolvedAssetPath;
        public string canonicalExportPath;
        public bool hasSlice;
        public Vector4 sliceBorder;
        public bool hasTint;
        public Color tintColor = Color.white;
        public float confidence;
        public string matchReason;
        public string rejectReason;
        public string diagnostics;

        public PSDImageReuseLog ToLog()
        {
            return new PSDImageReuseLog
            {
                sourceKind = sourceKind.ToString(),
                spritePath = spritePath,
                originalExportPath = originalExportPath,
                resolvedAssetPath = resolvedAssetPath,
                canonicalExportPath = canonicalExportPath,
                hasSlice = hasSlice,
                sliceBorder = hasSlice ? PSDImageBorderLog.From(sliceBorder) : null,
                hasTint = hasTint,
                tintColor = hasTint ? PSDImageTintLog.From(tintColor) : null,
                confidence = confidence,
                matchReason = matchReason,
                rejectReason = rejectReason,
                diagnostics = diagnostics
            };
        }
    }

    public static class PSDImageReuseLogStore
    {
        private static readonly Dictionary<string, PSDImageReuseLog> Logs =
            new Dictionary<string, PSDImageReuseLog>(StringComparer.OrdinalIgnoreCase);

        public static void Reset()
        {
            Logs.Clear();
        }

        public static void Record(PicData item, PSDImageReuseResult result)
        {
            if (result == null) return;
            Logs[BuildKey(item)] = result.ToLog();
        }

        public static PSDImageReuseLog Get(PicData item)
        {
            Logs.TryGetValue(BuildKey(item), out var log);
            return log;
        }

        public static string BuildSummary()
        {
            int total = 0;
            int original = 0;
            int local = 0;
            int common = 0;
            int sliced = 0;
            int missing = 0;
            foreach (var pair in Logs)
            {
                PSDImageReuseLog log = pair.Value;
                if (log == null) continue;
                total++;
                if (log.hasSlice) sliced++;
                if (string.Equals(log.sourceKind, PSDImageReuseSourceKind.OriginalExport.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    original++;
                }
                else if (string.Equals(log.sourceKind, PSDImageReuseSourceKind.LocalDuplicate.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    local++;
                }
                else if (string.Equals(log.sourceKind, PSDImageReuseSourceKind.Missing.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    missing++;
                }
                else if (!string.IsNullOrEmpty(log.sourceKind) && log.sourceKind.StartsWith("ProjectCommon", StringComparison.OrdinalIgnoreCase))
                {
                    common++;
                }
            }

            return $"images={total}, original={original}, localReuse={local}, commonReuse={common}, sliced={sliced}, missing={missing}";
        }

        public static PSDImageReuseLog GetOrBuildCurrent(
            PicData item,
            Transform node,
            string assetFolder,
            PSDImportConfig config)
        {
            var log = Get(item);
            if (log != null) return log;

            var image = node != null ? node.GetComponent<UISprite>() : null;
            if (image == null || image.atlas == null || string.IsNullOrEmpty(image.spriteName)) return null;

            // NGUI UISprite 通过 atlas + spriteName 引用；spritePath 用 atlas 资产路径 + spriteName
            string atlasPath = AssetDatabase.GetAssetPath(image.atlas);
            string spritePath = !string.IsNullOrEmpty(atlasPath) ? $"{atlasPath}/{image.spriteName}" : image.spriteName;
            string expectedPath = BuildPngPath(item, assetFolder);
            bool hasSlice = image.type == UISprite.Type.Sliced || image.border.sqrMagnitude > 0f;
            string kind = PSDImageReuseSourceKind.CurrentImage.ToString();
            bool hasTint = false;

            if (!string.IsNullOrEmpty(atlasPath))
            {
                if (string.Equals(NormalizeAssetPath(atlasPath), NormalizeAssetPath(expectedPath), StringComparison.OrdinalIgnoreCase))
                {
                    kind = PSDImageReuseSourceKind.OriginalExport.ToString();
                }
                else
                {
                    kind = PSDImageReuseSourceKind.LocalDuplicate.ToString();
                }
            }

            return new PSDImageReuseLog
            {
                sourceKind = kind,
                spritePath = spritePath,
                originalExportPath = expectedPath,
                resolvedAssetPath = atlasPath,
                canonicalExportPath = atlasPath,
                hasSlice = hasSlice,
                sliceBorder = hasSlice ? PSDImageBorderLog.From(image.border) : null,
                hasTint = hasTint,
                tintColor = hasTint ? PSDImageTintLog.From(image.color) : null,
                confidence = 1f,
                matchReason = "current image component"
            };
        }

        public static string BuildPngPath(PicData item, string assetFolder)
        {
            string group = item.groupName == "root/" ? "/" : item.groupName;
            string folder = (assetFolder ?? string.Empty).Replace("\\", "/");
            string groupedPath = $"{folder}{group}{item.pngName}.png".Replace("//", "/");
            if (AssetPathExists(groupedPath))
            {
                return groupedPath;
            }

            string flatPath = $"{folder}/{item.pngName}.png".Replace("//", "/");
            if (AssetPathExists(flatPath))
            {
                return flatPath;
            }

            return groupedPath;
        }

        private static string BuildKey(PicData item)
        {
            if (item.id != 0) return item.id.ToString();
            return (item.groupName ?? string.Empty) + "|" + (item.pngName ?? string.Empty);
        }

        private static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var normalized = path.Replace("\\", "/");
            var dataPath = Application.dataPath.Replace("\\", "/");
            if (normalized.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
            {
                return "Assets" + normalized.Substring(dataPath.Length);
            }
            if (Path.IsPathRooted(normalized)) return normalized;
            return normalized.TrimStart('/');
        }

        private static bool AssetPathExists(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string normalized = assetPath.Replace("\\", "/");
            string abs = normalized;
            if (!Path.IsPathRooted(abs))
            {
                string dataPath = Application.dataPath.Replace("\\", "/");
                if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    abs = dataPath.Substring(0, dataPath.Length - "Assets".Length) + normalized;
                }
                else
                {
                    abs = Path.GetFullPath(normalized).Replace("\\", "/");
                }
            }
            return File.Exists(abs);
        }

        private static bool ApproximatelyWhite(Color color)
        {
            return Mathf.Abs(color.r - 1f) < 0.001f &&
                   Mathf.Abs(color.g - 1f) < 0.001f &&
                   Mathf.Abs(color.b - 1f) < 0.001f &&
                   Mathf.Abs(color.a - 1f) < 0.001f;
        }
    }
}
