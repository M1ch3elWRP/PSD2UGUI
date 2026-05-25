using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace PSDImporter
{
    public static class PSDAssetDeduper
    {
        private const string MapFileName = "_psd_dedupe_map.tsv";
        private static readonly Dictionary<string, string> HashToPath = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> PathToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> PersistedPathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string currentScope;
        private static string persistedMapPath;
        private static bool mapDirty;

        public static void Reset()
        {
            HashToPath.Clear();
            PathToPath.Clear();
            PersistedPathMap.Clear();
            currentScope = null;
            persistedMapPath = null;
            mapDirty = false;
        }

        public static void EnsureScope(string rootFolder)
        {
            var normalized = NormalizeAssetPath(rootFolder ?? string.Empty);
            if (!string.Equals(currentScope, normalized, StringComparison.OrdinalIgnoreCase))
            {
                Reset();
                currentScope = normalized;
                persistedMapPath = BuildMapPath(normalized);
                LoadPersistedMap();
            }
        }

        public static string GetCanonicalPath(
            string assetPath,
            bool enabled,
            bool moveDuplicates,
            string moveFolder,
            string scopeRoot)
        {
            if (!enabled) return assetPath;
            if (string.IsNullOrEmpty(assetPath)) return assetPath;

            var normalizedPath = NormalizeAssetPath(assetPath);
            if (PathToPath.TryGetValue(normalizedPath, out var cached)) return cached;

            var absPath = GetAbsolutePath(normalizedPath);
            if (string.IsNullOrEmpty(absPath) || !File.Exists(absPath))
            {
                if (PersistedPathMap.TryGetValue(normalizedPath, out var mapped))
                {
                    var mappedAbs = GetAbsolutePath(mapped);
                    if (!string.IsNullOrEmpty(mappedAbs) && File.Exists(mappedAbs))
                    {
                        PathToPath[normalizedPath] = mapped;
                        return mapped;
                    }
                }
                PathToPath[normalizedPath] = normalizedPath;
                return normalizedPath;
            }

            var hash = ComputeHash(absPath);
            if (string.IsNullOrEmpty(hash))
            {
                PathToPath[normalizedPath] = normalizedPath;
                return normalizedPath;
            }

            if (HashToPath.TryGetValue(hash, out var canonical))
            {
                PathToPath[normalizedPath] = canonical;
                if (moveDuplicates)
                {
                    TryMoveDuplicate(normalizedPath, moveFolder, scopeRoot);
                    RecordPersistedMapping(normalizedPath, canonical);
                }
                return canonical;
            }

            HashToPath[hash] = normalizedPath;
            PathToPath[normalizedPath] = normalizedPath;
            return normalizedPath;
        }

        public static string GetCanonicalPath(string assetPath, bool enabled)
        {
            return GetCanonicalPath(assetPath, enabled, false, null, null);
        }

        public static string GetAbsolutePath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return assetPath;
            try
            {
                var normalized = assetPath.Replace("\\", "/");
                if (Path.IsPathRooted(normalized)) return normalized;
                return Path.GetFullPath(normalized).Replace("\\", "/");
            }
            catch
            {
                return assetPath;
            }
        }

        private static string NormalizeAssetPath(string path)
        {
            var normalized = path.Replace("\\", "/");
            var dataPath = Application.dataPath.Replace("\\", "/");
            if (normalized.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
            {
                return "Assets" + normalized.Substring(dataPath.Length);
            }
            return normalized;
        }

        private static string BuildMapPath(string scopeRoot)
        {
            if (string.IsNullOrEmpty(scopeRoot)) return null;
            var normalized = NormalizeAssetPath(scopeRoot);
            var absRoot = GetAbsolutePath(normalized);
            if (string.IsNullOrEmpty(absRoot)) return null;
            return Path.Combine(absRoot, MapFileName).Replace("\\", "/");
        }

        private static void LoadPersistedMap()
        {
            PersistedPathMap.Clear();
            if (string.IsNullOrEmpty(persistedMapPath) || !File.Exists(persistedMapPath)) return;

            try
            {
                var lines = File.ReadAllLines(persistedMapPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("#")) continue;
                    var parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    var src = NormalizeAssetPath(parts[0].Trim());
                    var dst = NormalizeAssetPath(parts[1].Trim());
                    if (!PersistedPathMap.ContainsKey(src)) PersistedPathMap.Add(src, dst);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PSDAssetDeduper] Load map failed: {persistedMapPath} ({ex.Message})");
            }
        }

        private static void RecordPersistedMapping(string sourcePath, string canonicalPath)
        {
            if (string.IsNullOrEmpty(persistedMapPath)) return;
            var src = NormalizeAssetPath(sourcePath);
            var dst = NormalizeAssetPath(canonicalPath);
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst)) return;
            PersistedPathMap[src] = dst;
            mapDirty = true;
            SavePersistedMap();
        }

        private static void SavePersistedMap()
        {
            if (!mapDirty) return;
            if (string.IsNullOrEmpty(persistedMapPath)) return;
            try
            {
                var dir = Path.GetDirectoryName(persistedMapPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var lines = new List<string> { "# PSDTools Dedupe Map v1 (src<TAB>canonical)" };
                foreach (var pair in PersistedPathMap)
                {
                    lines.Add(pair.Key + "\t" + pair.Value);
                }
                File.WriteAllLines(persistedMapPath, lines.ToArray());
                mapDirty = false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PSDAssetDeduper] Save map failed: {persistedMapPath} ({ex.Message})");
            }
        }

        private static void TryMoveDuplicate(string assetPath, string moveFolder, string scopeRoot)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            var normalized = NormalizeAssetPath(assetPath);
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(normalized, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var targetRoot = NormalizeAssetPath(scopeRoot ?? string.Empty);
            if (string.IsNullOrEmpty(targetRoot) || !targetRoot.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            {
                targetRoot = Path.GetDirectoryName(normalized)?.Replace("\\", "/") ?? "Assets";
            }

            var folderName = string.IsNullOrWhiteSpace(moveFolder) ? "_Duplicates" : moveFolder.Trim();
            var targetFolder = targetRoot.TrimEnd('/') + "/" + folderName;

            if (normalized.StartsWith(targetFolder + "/", StringComparison.OrdinalIgnoreCase)) return;

            EnsureAssetFolder(targetFolder);

            var fileName = Path.GetFileName(normalized);
            var targetPath = AssetDatabase.GenerateUniqueAssetPath(targetFolder + "/" + fileName);
            var moveResult = AssetDatabase.MoveAsset(normalized, targetPath);
            if (!string.IsNullOrEmpty(moveResult))
            {
                Debug.LogWarning($"[PSDAssetDeduper] Move duplicate failed: {normalized} -> {targetPath} ({moveResult})");
            }
        }

        private static void EnsureAssetFolder(string folderPath)
        {
            var normalized = folderPath.Replace("\\", "/");
            if (AssetDatabase.IsValidFolder(normalized)) return;

            var parent = Path.GetDirectoryName(normalized)?.Replace("\\", "/");
            var name = Path.GetFileName(normalized);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)) return;

            if (!AssetDatabase.IsValidFolder(parent)) EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        private static string ComputeHash(string filePath)
        {
            try
            {
                var visualHash = ComputeVisualHash(filePath);
                if (!string.IsNullOrEmpty(visualHash))
                {
                    return "VIS:" + visualHash;
                }

                using (var stream = File.OpenRead(filePath))
                using (var md5 = MD5.Create())
                {
                    var hash = md5.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PSDAssetDeduper] Hash failed: {filePath} ({ex.Message})");
                return null;
            }
        }

        private static string ComputeVisualHash(string filePath)
        {
            Texture2D tex = null;
            try
            {
                string ext = Path.GetExtension(filePath);
                if (!string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var bytes = File.ReadAllBytes(filePath);
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(bytes)) return null;

                var pixels = tex.GetPixels32();
                int width = tex.width;
                int height = tex.height;
                if (!TryTrimTransparent(pixels, width, height, out var trimmed, out var trimW, out var trimH))
                {
                    trimmed = pixels;
                    trimW = width;
                    trimH = height;
                }

                return ComputePixelHash(trimmed, trimW, trimH);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        private static bool TryTrimTransparent(Color32[] pixels, int width, int height, out Color32[] trimmed, out int trimW, out int trimH)
        {
            trimmed = null;
            trimW = 0;
            trimH = 0;
            if (pixels == null || pixels.Length != width * height || width <= 0 || height <= 0) return false;

            int minX = width;
            int minY = height;
            int maxX = -1;
            int maxY = -1;
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (pixels[row + x].a <= 0) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < minX || maxY < minY) return false;
            if (minX == 0 && minY == 0 && maxX == width - 1 && maxY == height - 1) return false;

            trimW = maxX - minX + 1;
            trimH = maxY - minY + 1;
            trimmed = new Color32[trimW * trimH];
            for (int y = 0; y < trimH; y++)
            {
                Array.Copy(pixels, (minY + y) * width + minX, trimmed, y * trimW, trimW);
            }
            return true;
        }

        private static string ComputePixelHash(Color32[] pixels, int width, int height)
        {
            if (pixels == null || pixels.Length == 0 || width <= 0 || height <= 0) return null;
            var byteCount = pixels.Length * 4 + 8;
            var buffer = new byte[byteCount];
            Buffer.BlockCopy(BitConverter.GetBytes(width), 0, buffer, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(height), 0, buffer, 4, 4);
            for (int i = 0; i < pixels.Length; i++)
            {
                int offset = 8 + i * 4;
                var c = pixels[i];
                buffer[offset] = c.r;
                buffer[offset + 1] = c.g;
                buffer[offset + 2] = c.b;
                buffer[offset + 3] = c.a;
            }

            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(buffer);
                return BitConverter.ToString(hash).Replace("-", "");
            }
        }
    }
}
