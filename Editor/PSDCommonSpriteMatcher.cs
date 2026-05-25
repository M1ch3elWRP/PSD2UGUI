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
        private static readonly string[] DefaultFolders =
        {
            "Assets/ArtWorks/UI/Resources/Mini/UITextures/Common",
            "Assets/ArtWorks/UI/Resources/Mini/UITextures/Common2",
            "Assets/ArtWorks/UI/Resources/Mini/UITextures/Panel",
            "Assets/ArtWorks/UI/Resources/Mini/UITextures/Panel2"
        };
        private const string OptimizeDrawcallFolderToken = "/__TexturesForOptimizeDrawcall/";
        private const string CommonSpriteTag = "@CommonSprite";
        private const string CommonSpriteWhiteTag = "@CommonSpriteWhite";

        private class SpriteHashEntry
        {
            public Sprite Sprite;
            public string AssetPath;
            public string ExactHash;
            public string TrimmedHash;
            public string AlphaShapeHash;
            public ulong DHash;
            public Color32[] Pixels;
            public int Width;
            public int Height;
            public Vector4 Border;
            public bool HasBorder;
            public bool IsTintableWhite;
            public readonly Dictionary<string, ulong> SlicedHashCache = new Dictionary<string, ulong>();
        }

        private static readonly Dictionary<string, SpriteHashEntry> ExactHashToEntry =
            new Dictionary<string, SpriteHashEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<SpriteHashEntry>> AlphaShapeHashToEntries =
            new Dictionary<string, List<SpriteHashEntry>>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<SpriteHashEntry> SpriteEntries = new List<SpriteHashEntry>();
        private static string lastFolderKey;
        private static bool indexBuilt;
        private static bool warnedNoFolder;
        private static bool warnedNoSprite;

        public static void Reset()
        {
            ExactHashToEntry.Clear();
            AlphaShapeHashToEntries.Clear();
            SpriteEntries.Clear();
            lastFolderKey = null;
            indexBuilt = false;
            warnedNoFolder = false;
            warnedNoSprite = false;
        }

        public static bool TryResolveCommonSprite(string pngAssetPath, PSDImportConfig config, out Sprite sprite)
        {
            sprite = null;
            if (TryResolveCommonSprite(pngAssetPath, default(PicData), config, out var result) && result != null)
            {
                sprite = result.sprite;
                return sprite != null;
            }
            return false;
        }

        public static bool TryResolveCommonSprite(
            string pngAssetPath,
            PicData item,
            PSDImportConfig config,
            out PSDImageReuseResult result)
        {
            result = null;
            if (config == null || !config.commonSpriteMatch) return false;

            if (string.IsNullOrEmpty(pngAssetPath)) return false;
            var abs = PSDAssetDeduper.GetAbsolutePath(pngAssetPath);
            if (string.IsNullOrEmpty(abs) || !File.Exists(abs)) return false;

            if (!TryLoadPixels(abs, out var pixels, out var width, out var height)) return false;

            bool taggedWhite = PSDTagUtility.HasTag(item.pngName, CommonSpriteWhiteTag);
            bool taggedCommon = PSDTagUtility.HasTag(item.pngName, CommonSpriteTag);
            PSDCommonSpriteMatchMode mode = config.commonSpriteMatchMode;
            bool allowTagged = taggedCommon;
            bool allowAuto = mode == PSDCommonSpriteMatchMode.AutoAndTagged ||
                             mode == PSDCommonSpriteMatchMode.Aggressive;
            bool allowAggressive = mode == PSDCommonSpriteMatchMode.Aggressive;

            if (taggedWhite)
            {
                EnsureIndex(config, true);
                if (SpriteEntries.Count == 0) return false;
                return TryResolveTintedCommon(pixels, width, height, pngAssetPath, out result);
            }

            EnsureIndex(config, false);
            if (SpriteEntries.Count == 0) return false;

            if (!allowTagged && !allowAuto) return false;

            var exact = ComputePixelHash(pixels, width, height);
            var trimmed = ComputeTrimmedPixelHash(pixels, width, height);
            if (TryGetExactEntry(exact, trimmed, out var exactEntry))
            {
                result = BuildResult(
                    exactEntry,
                    PSDImageReuseSourceKind.ProjectCommonExact,
                    pngAssetPath,
                    1f,
                    "project common exact hash");
                return true;
            }

            if (allowTagged || allowAuto)
            {
                if (TryResolveSliced(abs, pixels, width, height, item, config, taggedCommon, out result))
                {
                    result.originalExportPath = pngAssetPath;
                    return true;
                }
            }

            if (allowTagged || allowAggressive)
            {
                if (TryResolvePerceptual(pixels, width, height, config, taggedCommon, pngAssetPath, out result))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsCommonSpritePath(string assetPath, PSDImportConfig config)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string normalized = NormalizeAssetPath(assetPath);
            if (IsExcludedCommonSpritePath(normalized)) return false;
            return IsPathInFolders(normalized, NormalizeFolders(config != null ? config.commonSpriteFolders : null, true)) ||
                   IsPathInFolders(normalized, GetWhiteFolders(config, false));
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

        private static bool TryResolveTintedCommon(
            Color32[] pixels,
            int width,
            int height,
            string pngAssetPath,
            out PSDImageReuseResult result)
        {
            result = null;
            string alphaHash = ComputeTrimmedAlphaHash(pixels, width, height);
            if (string.IsNullOrEmpty(alphaHash)) return false;
            if (!AlphaShapeHashToEntries.TryGetValue(alphaHash, out var entries) || entries == null || entries.Count == 0)
            {
                return false;
            }

            var tintable = new List<SpriteHashEntry>();
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry != null && entry.IsTintableWhite)
                {
                    tintable.Add(entry);
                }
            }

            if (tintable.Count == 0) return false;

            if (!TryComputeSolidTintColor(pixels, out var tintColor, out var tintDiagnostics)) return false;

            var best = tintable[0];
            result = BuildResult(
                best,
                PSDImageReuseSourceKind.ProjectCommonTint,
                pngAssetPath,
                1f,
                "project common alpha-shape tint match");
            result.hasTint = true;
            result.tintColor = tintColor;
            result.diagnostics = tintable.Count > 1
                ? $"alphaShape={alphaHash}; ambiguous={tintable.Count}; selected={best.AssetPath}; {tintDiagnostics}"
                : $"alphaShape={alphaHash}; {tintDiagnostics}";
            return true;
        }

        private static bool TryResolveSliced(
            string abs,
            Color32[] targetPixels,
            int targetWidth,
            int targetHeight,
            PicData item,
            PSDImportConfig config,
            bool taggedCommon,
            out PSDImageReuseResult result)
        {
            result = null;
            if (config == null || config.commonSpritePerceptualThreshold <= 0) return false;

            bool targetLooksSliceable = item.hasSlice;
            if (!targetLooksSliceable)
            {
                Vector4 ignoredBorder;
                targetLooksSliceable = PSDNineSliceUtility.TryDetectBorder(abs, out ignoredBorder);
            }
            if (!targetLooksSliceable) return false;

            int displayW = Mathf.RoundToInt(item.width);
            int displayH = Mathf.RoundToInt(item.height);
            if (displayW <= 0) displayW = targetWidth;
            if (displayH <= 0) displayH = targetHeight;

            ulong targetHash = ComputeDHash(targetPixels, targetWidth, targetHeight);
            int threshold = taggedCommon
                ? config.commonSpritePerceptualThreshold
                : Mathf.Min(4, config.commonSpritePerceptualThreshold);

            SpriteHashEntry best = null;
            int bestDist = int.MaxValue;
            for (int i = 0; i < SpriteEntries.Count; i++)
            {
                var entry = SpriteEntries[i];
                if (entry.IsTintableWhite) continue;
                if (!entry.HasBorder || entry.Pixels == null || entry.Pixels.Length == 0) continue;
                ulong candidateHash = GetSlicedDHash(entry, displayW, displayH);
                int dist = HammingDistance(targetHash, candidateHash);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = entry;
                    if (bestDist == 0) break;
                }
            }

            if (best == null || bestDist > threshold) return false;

            result = BuildResult(
                best,
                PSDImageReuseSourceKind.ProjectCommonSliced,
                abs,
                1f - bestDist / 64f,
                $"project common sliced dHash distance={bestDist}");
            result.diagnostics = $"display={displayW}x{displayH}; threshold={threshold}";
            return true;
        }

        private static bool TryResolvePerceptual(
            Color32[] pixels,
            int width,
            int height,
            PSDImportConfig config,
            bool taggedCommon,
            string pngAssetPath,
            out PSDImageReuseResult result)
        {
            result = null;
            if (config == null || config.commonSpritePerceptualThreshold <= 0) return false;

            int threshold = taggedCommon
                ? config.commonSpritePerceptualThreshold
                : Mathf.Min(4, config.commonSpritePerceptualThreshold);
            ulong targetHash = ComputeDHash(pixels, width, height);

            int bestDist = int.MaxValue;
            SpriteHashEntry best = null;
            for (int i = 0; i < SpriteEntries.Count; i++)
            {
                var entry = SpriteEntries[i];
                if (entry.IsTintableWhite) continue;
                int dist = HammingDistance(targetHash, entry.DHash);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = entry;
                    if (bestDist == 0) break;
                }
            }

            if (best == null || bestDist > threshold) return false;

            result = BuildResult(
                best,
                PSDImageReuseSourceKind.ProjectCommonPerceptual,
                pngAssetPath,
                1f - bestDist / 64f,
                $"project common perceptual dHash distance={bestDist}");
            result.diagnostics = $"threshold={threshold}";
            return true;
        }

        private static PSDImageReuseResult BuildResult(
            SpriteHashEntry entry,
            PSDImageReuseSourceKind sourceKind,
            string originalExportPath,
            float confidence,
            string reason)
        {
            return new PSDImageReuseResult
            {
                sourceKind = sourceKind,
                sprite = entry.Sprite,
                spritePath = entry.AssetPath,
                originalExportPath = originalExportPath,
                resolvedAssetPath = entry.AssetPath,
                canonicalExportPath = entry.AssetPath,
                hasSlice = entry.HasBorder,
                sliceBorder = entry.Border,
                hasTint = false,
                tintColor = Color.white,
                confidence = confidence,
                matchReason = reason
            };
        }

        private static bool TryGetExactEntry(string exact, string trimmed, out SpriteHashEntry entry)
        {
            if (!string.IsNullOrEmpty(exact) && ExactHashToEntry.TryGetValue(exact, out entry) && entry != null)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(trimmed) && ExactHashToEntry.TryGetValue(trimmed, out entry) && entry != null)
            {
                return true;
            }

            entry = null;
            return false;
        }

        private static void EnsureIndex(PSDImportConfig config, bool whiteMode)
        {
            var folders = whiteMode ? GetWhiteFolders(config, true) : NormalizeFolders(config.commonSpriteFolders, true);
            var folderKey = (whiteMode ? "white:" : "common:") + string.Join("|", folders);
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
                if (IsExcludedCommonSpritePath(path)) continue;

                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite == null) continue;

                try
                {
                    if (!TryLoadSpritePixels(path, sprite, out var pixels, out var width, out var height))
                    {
                        continue;
                    }

                    var exact = ComputePixelHash(pixels, width, height);
                    if (string.IsNullOrEmpty(exact)) continue;

                    var entry = new SpriteHashEntry
                    {
                        Sprite = sprite,
                        AssetPath = path,
                        ExactHash = exact,
                        TrimmedHash = ComputeTrimmedPixelHash(pixels, width, height),
                        AlphaShapeHash = ComputeTrimmedAlphaHash(pixels, width, height),
                        DHash = ComputeDHash(pixels, width, height),
                        Pixels = pixels,
                        Width = width,
                        Height = height,
                        Border = sprite.border,
                        HasBorder = sprite.border.sqrMagnitude > 0f,
                        IsTintableWhite = IsTintableWhiteSprite(pixels)
                    };

                    SpriteEntries.Add(entry);
                    AddExactEntry(entry.ExactHash, entry);
                    AddExactEntry(entry.TrimmedHash, entry);
                    AddAlphaShapeEntry(entry.AlphaShapeHash, entry);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PSDCommonSpriteMatcher] Skip unreadable common sprite: {path} ({ex.Message})");
                }
            }

            if (SpriteEntries.Count == 0 && !warnedNoSprite)
            {
                Debug.Log("[PSDCommonSpriteMatcher] Common sprite match disabled: no sprites found in configured folders.");
                warnedNoSprite = true;
            }
        }

        private static void AddExactEntry(string hash, SpriteHashEntry entry)
        {
            if (string.IsNullOrEmpty(hash) || entry == null) return;
            if (!ExactHashToEntry.ContainsKey(hash))
            {
                ExactHashToEntry.Add(hash, entry);
            }
        }

        private static void AddAlphaShapeEntry(string hash, SpriteHashEntry entry)
        {
            if (string.IsNullOrEmpty(hash) || entry == null) return;
            if (!AlphaShapeHashToEntries.TryGetValue(hash, out var entries))
            {
                entries = new List<SpriteHashEntry>();
                AlphaShapeHashToEntries.Add(hash, entries);
            }
            entries.Add(entry);
        }

        private static string[] NormalizeFolders(string[] folders, bool useDefaultWhenEmpty)
        {
            if ((folders == null || folders.Length == 0) && useDefaultWhenEmpty)
            {
                folders = DefaultFolders;
            }

            if (folders == null || folders.Length == 0) return new string[0];
            var list = new List<string>();
            for (int i = 0; i < folders.Length; i++)
            {
                var normalized = NormalizeAssetPath(folders[i]);
                if (string.IsNullOrEmpty(normalized)) continue;
                if (IsExcludedCommonSpritePath(normalized)) continue;
                if (AssetDatabase.IsValidFolder(normalized))
                {
                    list.Add(normalized.TrimEnd('/'));
                }
            }
            return list.ToArray();
        }

        private static string[] GetWhiteFolders(PSDImportConfig config, bool fallbackToCommonWhenEmpty)
        {
            bool hasConfiguredWhiteFolders = false;
            if (config != null && config.commonSpriteWhiteFolders != null)
            {
                for (int i = 0; i < config.commonSpriteWhiteFolders.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(config.commonSpriteWhiteFolders[i]))
                    {
                        hasConfiguredWhiteFolders = true;
                        break;
                    }
                }
            }

            var whiteFolders = NormalizeFolders(config != null ? config.commonSpriteWhiteFolders : null, false);
            if (whiteFolders.Length > 0 || hasConfiguredWhiteFolders || !fallbackToCommonWhenEmpty)
            {
                return whiteFolders;
            }

            return NormalizeFolders(config != null ? config.commonSpriteFolders : null, true);
        }

        private static bool IsPathInFolders(string normalizedPath, string[] folders)
        {
            if (string.IsNullOrEmpty(normalizedPath) || folders == null) return false;
            for (int i = 0; i < folders.Length; i++)
            {
                if (normalizedPath.StartsWith(folders[i].TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsExcludedCommonSpritePath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string normalized = assetPath.Replace("\\", "/");
            if (!normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized += "/";
            }
            return normalized.IndexOf(OptimizeDrawcallFolderToken, StringComparison.OrdinalIgnoreCase) >= 0;
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
            Texture2D tex = null;
            try
            {
                var data = File.ReadAllBytes(absPath);
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(data)) return false;
                width = tex.width;
                height = tex.height;
                pixels = tex.GetPixels32();
                return pixels != null && pixels.Length > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        private static bool TryLoadSpritePixels(
            string assetPath,
            Sprite sprite,
            out Color32[] pixels,
            out int width,
            out int height)
        {
            pixels = null;
            width = 0;
            height = 0;

            string abs = PSDAssetDeduper.GetAbsolutePath(assetPath);
            if (string.IsNullOrEmpty(abs) || !File.Exists(abs)) return false;
            if (!TryLoadPixels(abs, out var sourcePixels, out var sourceW, out var sourceH)) return false;

            if (sprite == null)
            {
                pixels = sourcePixels;
                width = sourceW;
                height = sourceH;
                return true;
            }

            Rect rect = sprite.rect;
            int x = Mathf.RoundToInt(rect.x);
            int y = Mathf.RoundToInt(rect.y);
            int w = Mathf.RoundToInt(rect.width);
            int h = Mathf.RoundToInt(rect.height);
            if (w <= 0 || h <= 0) return false;

            if (x == 0 && y == 0 && w == sourceW && h == sourceH)
            {
                pixels = sourcePixels;
                width = sourceW;
                height = sourceH;
                return true;
            }

            if (x < 0 || y < 0 || x + w > sourceW || y + h > sourceH) return false;

            var cropped = new Color32[w * h];
            for (int row = 0; row < h; row++)
            {
                Array.Copy(sourcePixels, (y + row) * sourceW + x, cropped, row * w, w);
            }

            pixels = cropped;
            width = w;
            height = h;
            return true;
        }

        private static string ComputeTrimmedPixelHash(Color32[] pixels, int width, int height)
        {
            if (!TryTrimTransparent(pixels, width, height, out var trimmed, out var trimW, out var trimH))
            {
                return null;
            }
            return ComputePixelHash(trimmed, trimW, trimH);
        }

        private static string ComputeTrimmedAlphaHash(Color32[] pixels, int width, int height)
        {
            if (!TryTrimTransparent(pixels, width, height, out var trimmed, out var trimW, out var trimH))
            {
                trimmed = pixels;
                trimW = width;
                trimH = height;
            }
            return ComputeAlphaHash(trimmed, trimW, trimH);
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

        private static string ComputeAlphaHash(Color32[] pixels, int width, int height)
        {
            if (pixels == null || pixels.Length == 0 || width <= 0 || height <= 0) return null;
            try
            {
                var byteCount = pixels.Length + 8;
                var buffer = new byte[byteCount];
                Buffer.BlockCopy(BitConverter.GetBytes(width), 0, buffer, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(height), 0, buffer, 4, 4);
                for (int i = 0; i < pixels.Length; i++)
                {
                    buffer[8 + i] = pixels[i].a > 0 ? (byte)1 : (byte)0;
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

        private static bool IsTintableWhiteSprite(Color32[] pixels)
        {
            if (pixels == null || pixels.Length == 0) return false;

            int count = 0;
            long totalR = 0;
            long totalG = 0;
            long totalB = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                if (c.a <= 0) continue;
                count++;
                totalR += c.r;
                totalG += c.g;
                totalB += c.b;
            }

            if (count == 0) return false;
            float inv = 1f / count;
            return totalR * inv >= 245f &&
                   totalG * inv >= 245f &&
                   totalB * inv >= 245f;
        }

        private static bool TryComputeSolidTintColor(Color32[] pixels, out Color color, out string diagnostics)
        {
            color = Color.white;
            diagnostics = null;
            if (pixels == null || pixels.Length == 0)
            {
                diagnostics = "solidTint=none";
                return false;
            }

            double totalWeight = 0d;
            double totalR = 0d;
            double totalG = 0d;
            double totalB = 0d;
            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                if (c.a <= 4) continue;
                double weight = c.a / 255d;
                totalWeight += weight;
                totalR += c.r * weight;
                totalG += c.g * weight;
                totalB += c.b * weight;
            }

            if (totalWeight <= 0d)
            {
                diagnostics = "solidTint=noVisiblePixels";
                return false;
            }

            float avgR = (float)(totalR / totalWeight);
            float avgG = (float)(totalG / totalWeight);
            float avgB = (float)(totalB / totalWeight);
            float maxDeviation = 0f;
            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                if (c.a <= 4) continue;
                maxDeviation = Mathf.Max(maxDeviation, Mathf.Abs(c.r - avgR));
                maxDeviation = Mathf.Max(maxDeviation, Mathf.Abs(c.g - avgG));
                maxDeviation = Mathf.Max(maxDeviation, Mathf.Abs(c.b - avgB));
                if (maxDeviation > 16f)
                {
                    diagnostics = $"solidTint=rejected; maxDeviation={maxDeviation:F1}";
                    return false;
                }
            }

            color = new Color(
                Mathf.Clamp01(avgR / 255f),
                Mathf.Clamp01(avgG / 255f),
                Mathf.Clamp01(avgB / 255f),
                1f);
            diagnostics = $"solidTint=accepted; maxDeviation={maxDeviation:F1}";
            return true;
        }

        private static ulong GetSlicedDHash(SpriteHashEntry entry, int displayW, int displayH)
        {
            string key = displayW + "x" + displayH;
            if (entry.SlicedHashCache.TryGetValue(key, out var hash)) return hash;

            Color32[] preview = RenderSlicedPreview(entry, displayW, displayH, 32, 32);
            hash = ComputeDHash(preview, 32, 32);
            entry.SlicedHashCache[key] = hash;
            return hash;
        }

        private static Color32[] RenderSlicedPreview(SpriteHashEntry entry, int displayW, int displayH, int previewW, int previewH)
        {
            var result = new Color32[previewW * previewH];
            if (entry.Pixels == null || entry.Pixels.Length == 0 || entry.Width <= 0 || entry.Height <= 0)
            {
                return result;
            }

            displayW = Mathf.Max(1, displayW);
            displayH = Mathf.Max(1, displayH);

            float left = Mathf.Clamp(entry.Border.x, 0, entry.Width * 0.5f);
            float right = Mathf.Clamp(entry.Border.z, 0, entry.Width * 0.5f);
            float bottom = Mathf.Clamp(entry.Border.y, 0, entry.Height * 0.5f);
            float top = Mathf.Clamp(entry.Border.w, 0, entry.Height * 0.5f);

            for (int y = 0; y < previewH; y++)
            {
                float outY = (y + 0.5f) * displayH / previewH;
                int srcY = Mathf.RoundToInt(MapSlicedAxis(outY, displayH, entry.Height, bottom, top));
                srcY = Mathf.Clamp(srcY, 0, entry.Height - 1);
                for (int x = 0; x < previewW; x++)
                {
                    float outX = (x + 0.5f) * displayW / previewW;
                    int srcX = Mathf.RoundToInt(MapSlicedAxis(outX, displayW, entry.Width, left, right));
                    srcX = Mathf.Clamp(srcX, 0, entry.Width - 1);
                    result[y * previewW + x] = entry.Pixels[srcY * entry.Width + srcX];
                }
            }

            return result;
        }

        private static float MapSlicedAxis(float outputPos, int outputSize, int sourceSize, float nearBorder, float farBorder)
        {
            float outFarStart = Mathf.Max(nearBorder, outputSize - farBorder);
            if (outputPos < nearBorder) return outputPos;
            if (outputPos >= outFarStart) return sourceSize - (outputSize - outputPos);

            float sourceCenter = Mathf.Max(1f, sourceSize - nearBorder - farBorder);
            float outputCenter = Mathf.Max(1f, outputSize - nearBorder - farBorder);
            float t = Mathf.Clamp01((outputPos - nearBorder) / outputCenter);
            return nearBorder + t * sourceCenter;
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
