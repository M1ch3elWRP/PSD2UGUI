using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace PSDImporter
{
    public static class PSDCommonSpriteMatcher
    {
        private class SpriteHashEntry
        {
            public Sprite Sprite;
            public string ExactHash;
            public ulong DHash;
        }

        private static readonly Dictionary<string, Sprite> ExactHashToSprite = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<SpriteHashEntry> SpriteEntries = new List<SpriteHashEntry>();
        private static string lastFolderKey;
        private static bool indexBuilt;
        private static bool warnedNoFolder;
        private static bool warnedNoSprite;

        public static void Reset()
        {
            ExactHashToSprite.Clear();
            SpriteEntries.Clear();
            lastFolderKey = null;
            indexBuilt = false;
            warnedNoFolder = false;
            warnedNoSprite = false;
        }

        public static bool TryResolveCommonSprite(string pngAssetPath, PSDImportConfig config, out Sprite sprite)
        {
            sprite = null;
            if (config == null || !config.commonSpriteMatch) return false;

            EnsureIndex(config);
            if (SpriteEntries.Count == 0) return false;

            if (string.IsNullOrEmpty(pngAssetPath)) return false;
            var abs = PSDAssetDeduper.GetAbsolutePath(pngAssetPath);
            if (string.IsNullOrEmpty(abs) || !File.Exists(abs)) return false;

            if (!TryLoadPixels(abs, out var pixels, out var width, out var height)) return false;

            var exact = ComputePixelHash(pixels, width, height);
            if (!string.IsNullOrEmpty(exact) && ExactHashToSprite.TryGetValue(exact, out sprite) && sprite != null)
            {
                return true;
            }

            if (config.commonSpritePerceptualThreshold <= 0) return false;
            var targetHash = ComputeDHash(pixels, width, height);

            int bestDist = int.MaxValue;
            Sprite best = null;
            for (int i = 0; i < SpriteEntries.Count; i++)
            {
                var entry = SpriteEntries[i];
                var dist = HammingDistance(targetHash, entry.DHash);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = entry.Sprite;
                    if (bestDist == 0) break;
                }
            }

            if (best != null && bestDist <= config.commonSpritePerceptualThreshold)
            {
                sprite = best;
                return true;
            }

            return false;
        }

        public static void MoveMatchedExport(string pngAssetPath, string psdAssetFolder, PSDImportConfig config)
        {
            if (config == null || !config.commonSpriteMoveMatched) return;
            if (string.IsNullOrEmpty(pngAssetPath)) return;

            var normalized = NormalizeAssetPath(pngAssetPath);
            if (string.IsNullOrEmpty(normalized)) return;
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(normalized, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var targetRoot = NormalizeAssetPath(psdAssetFolder ?? string.Empty);
            if (string.IsNullOrEmpty(targetRoot) || !targetRoot.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            {
                targetRoot = Path.GetDirectoryName(normalized)?.Replace("\\", "/") ?? "Assets";
            }

            var folderName = string.IsNullOrWhiteSpace(config.commonSpriteMoveFolder) ? "_CommonMatched" : config.commonSpriteMoveFolder.Trim();
            var targetFolder = targetRoot.TrimEnd('/') + "/" + folderName;
            if (normalized.StartsWith(targetFolder + "/", StringComparison.OrdinalIgnoreCase)) return;

            EnsureAssetFolder(targetFolder);

            var fileName = Path.GetFileName(normalized);
            var targetPath = AssetDatabase.GenerateUniqueAssetPath(targetFolder + "/" + fileName);
            var moveResult = AssetDatabase.MoveAsset(normalized, targetPath);
            if (!string.IsNullOrEmpty(moveResult))
            {
                Debug.LogWarning($"[PSDCommonSpriteMatcher] Move matched export failed: {normalized} -> {targetPath} ({moveResult})");
            }
        }

        private static void EnsureIndex(PSDImportConfig config)
        {
            var folders = NormalizeFolders(config.commonSpriteFolders);
            var folderKey = string.Join("|", folders);
            if (indexBuilt && string.Equals(folderKey, lastFolderKey, StringComparison.OrdinalIgnoreCase)) return;

            Reset();
            lastFolderKey = folderKey;
            indexBuilt = true;

            if (folders.Length == 0)
            {
                if (!warnedNoFolder)
                {
                    Debug.Log("[PSDCommonSpriteMatcher] Common sprite match disabled: no valid folders configured.");
                    warnedNoFolder = true;
                }
                return;
            }

            var guids = AssetDatabase.FindAssets("t:Sprite", folders);
            for (int i = 0; i < guids.Length; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite == null) continue;

                Texture2D tex = null;
                try
                {
                    tex = UnityEditor.Sprites.SpriteUtility.GetSpriteTexture(sprite, false);
                    if (tex == null) continue;
                    var pixels = tex.GetPixels32();
                    var exact = ComputePixelHash(pixels, tex.width, tex.height);
                    if (string.IsNullOrEmpty(exact)) continue;

                    var entry = new SpriteHashEntry
                    {
                        Sprite = sprite,
                        ExactHash = exact,
                        DHash = ComputeDHash(pixels, tex.width, tex.height)
                    };
                    SpriteEntries.Add(entry);
                    if (!ExactHashToSprite.ContainsKey(exact))
                    {
                        ExactHashToSprite.Add(exact, sprite);
                    }
                }
                finally
                {
                    if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                }
            }

            if (SpriteEntries.Count == 0 && !warnedNoSprite)
            {
                Debug.Log("[PSDCommonSpriteMatcher] Common sprite match disabled: no sprites found in configured folders.");
                warnedNoSprite = true;
            }
        }

        private static string[] NormalizeFolders(string[] folders)
        {
            if (folders == null || folders.Length == 0) return new string[0];
            var list = new List<string>();
            for (int i = 0; i < folders.Length; i++)
            {
                var normalized = NormalizeAssetPath(folders[i]);
                if (string.IsNullOrEmpty(normalized)) continue;
                if (AssetDatabase.IsValidFolder(normalized))
                {
                    list.Add(normalized.TrimEnd('/'));
                }
            }
            return list.ToArray();
        }

        private static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var normalized = path.Replace("\\", "/").Trim();
            if (Path.IsPathRooted(normalized))
            {
                var dataPath = Application.dataPath.Replace("\\", "/");
                if (normalized.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
                {
                    return "Assets" + normalized.Substring(dataPath.Length);
                }
                return null;
            }
            if (!normalized.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "Assets/" + normalized.TrimStart('/');
            }
            return normalized;
        }

        private static bool TryLoadPixels(string absPath, out Color32[] pixels, out int width, out int height)
        {
            pixels = null;
            width = 0;
            height = 0;
            try
            {
                var data = File.ReadAllBytes(absPath);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(data))
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                    return false;
                }
                width = tex.width;
                height = tex.height;
                pixels = tex.GetPixels32();
                UnityEngine.Object.DestroyImmediate(tex);
                return pixels != null && pixels.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static string ComputePixelHash(Color32[] pixels, int width, int height)
        {
            if (pixels == null || pixels.Length == 0 || width <= 0 || height <= 0) return null;
            try
            {
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
            catch
            {
                return null;
            }
        }

        private static ulong ComputeDHash(Color32[] pixels, int width, int height)
        {
            if (pixels == null || pixels.Length == 0 || width <= 0 || height <= 0) return 0;

            const int hashW = 9;
            const int hashH = 8;
            ulong hash = 0;
            int bit = 0;

            for (int y = 0; y < hashH; y++)
            {
                for (int x = 0; x < hashW - 1; x++)
                {
                    float l = SampleLuma(pixels, width, height, x, y, hashW, hashH);
                    float r = SampleLuma(pixels, width, height, x + 1, y, hashW, hashH);
                    if (l > r) hash |= 1UL << bit;
                    bit++;
                }
            }

            return hash;
        }

        private static float SampleLuma(Color32[] pixels, int width, int height, int x, int y, int sampleW, int sampleH)
        {
            int srcX = (sampleW <= 1) ? 0 : (x * (width - 1) / (sampleW - 1));
            int srcY = (sampleH <= 1) ? 0 : (y * (height - 1) / (sampleH - 1));
            int idx = srcY * width + srcX;
            if (idx < 0 || idx >= pixels.Length) return 0f;
            var c = pixels[idx];
            float a = c.a / 255f;
            return (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) * a;
        }

        private static int HammingDistance(ulong a, ulong b)
        {
            ulong v = a ^ b;
            int count = 0;
            while (v != 0)
            {
                v &= v - 1;
                count++;
            }
            return count;
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
    }
}
