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
        private static readonly Dictionary<string, string> HashToPath = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> PathToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string currentScope;

        public static void Reset()
        {
            HashToPath.Clear();
            PathToPath.Clear();
            currentScope = null;
        }

        public static void EnsureScope(string rootFolder)
        {
            var normalized = NormalizeAssetPath(rootFolder ?? string.Empty);
            if (!string.Equals(currentScope, normalized, StringComparison.OrdinalIgnoreCase))
            {
                Reset();
                currentScope = normalized;
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
                if (moveDuplicates) TryMoveDuplicate(normalizedPath, moveFolder, scopeRoot);
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
    }
}
