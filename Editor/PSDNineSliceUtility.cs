using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PSDImporter
{
    public static class PSDNineSliceUtility
    {
        private const float MinBorderRatio = 0.08f;
        private const float MaxBorderRatio = 0.40f;
        private const int MinBorderPx = 6;
        private const int SampleCount = 24;
        private const int Tolerance = 6;
        private const float FlatRatio = 0.9f;

        private struct CachedBorder
        {
            public Vector4 Border;
            public DateTime LastWriteUtc;
        }

        private static readonly Dictionary<string, CachedBorder> BorderCache =
            new Dictionary<string, CachedBorder>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, DateTime> NoBorderCache =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public static bool TryDetectBorder(string pngPath, out Vector4 border)
        {
            border = Vector4.zero;
            if (string.IsNullOrEmpty(pngPath) || !File.Exists(pngPath)) return false;

            DateTime lastWrite = File.GetLastWriteTimeUtc(pngPath);
            if (BorderCache.TryGetValue(pngPath, out var cachedBorder) && cachedBorder.LastWriteUtc == lastWrite)
            {
                border = cachedBorder.Border;
                return true;
            }

            if (NoBorderCache.TryGetValue(pngPath, out var cachedNo) && cachedNo == lastWrite) return false;

            if (!TryLoadPixels(pngPath, out var pixels, out var width, out var height))
            {
                NoBorderCache[pngPath] = lastWrite;
                return false;
            }

            if (!TryDetectBorder(pixels, width, height, out border))
            {
                NoBorderCache[pngPath] = lastWrite;
                return false;
            }

            BorderCache[pngPath] = new CachedBorder { Border = border, LastWriteUtc = lastWrite };
            return true;
        }

        private static bool TryLoadPixels(string path, out Color32[] pixels, out int width, out int height)
        {
            pixels = null;
            width = 0;
            height = 0;
            Texture2D temp = null;
            try
            {
                var bytes = File.ReadAllBytes(path);
                temp = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                if (!temp.LoadImage(bytes, false)) return false;
                width = temp.width;
                height = temp.height;
                pixels = temp.GetPixels32();
                return pixels != null && pixels.Length == width * height;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (temp != null) UnityEngine.Object.DestroyImmediate(temp);
            }
        }

        private static bool TryDetectBorder(Color32[] pixels, int width, int height, out Vector4 border)
        {
            border = Vector4.zero;
            if (width < 8 || height < 8) return false;

            int minDim = Mathf.Min(width, height);
            int minBorder = Mathf.Max(MinBorderPx, Mathf.RoundToInt(minDim * MinBorderRatio));
            int maxBorder = Mathf.Max(minBorder, Mathf.RoundToInt(minDim * MaxBorderRatio));

            int left = DetectBorderColumn(pixels, width, height, true, minBorder, maxBorder);
            int right = DetectBorderColumn(pixels, width, height, false, minBorder, maxBorder);
            int top = DetectBorderRow(pixels, width, height, true, minBorder, maxBorder);
            int bottom = DetectBorderRow(pixels, width, height, false, minBorder, maxBorder);

            if (left <= 0 || right <= 0 || top <= 0 || bottom <= 0) return false;
            if ((left + right) >= (width - 4)) return false;
            if ((top + bottom) >= (height - 4)) return false;

            int centerW = width - left - right;
            int centerH = height - top - bottom;
            if (centerW < 4 || centerH < 4) return false;

            border = new Vector4(left, bottom, right, top);
            return true;
        }

        private static int DetectBorderRow(Color32[] pixels, int width, int height, bool isTop, int minBorder, int maxBorder)
        {
            Color32? baseColor = null;
            int thickness = 0;
            int maxCheck = Mathf.Min(maxBorder, height);
            for (int d = 0; d < maxCheck; d++)
            {
                int y = isTop ? (height - 1 - d) : d;
                if (!IsLineFlat(pixels, width, height, true, y, width, baseColor, out var lineColor))
                    break;

                if (!baseColor.HasValue) baseColor = lineColor;
                thickness++;
            }
            return thickness >= minBorder ? thickness : 0;
        }

        private static int DetectBorderColumn(Color32[] pixels, int width, int height, bool isLeft, int minBorder, int maxBorder)
        {
            Color32? baseColor = null;
            int thickness = 0;
            int maxCheck = Mathf.Min(maxBorder, width);
            for (int d = 0; d < maxCheck; d++)
            {
                int x = isLeft ? d : (width - 1 - d);
                if (!IsLineFlat(pixels, width, height, false, x, height, baseColor, out var lineColor))
                    break;

                if (!baseColor.HasValue) baseColor = lineColor;
                thickness++;
            }
            return thickness >= minBorder ? thickness : 0;
        }

        private static bool IsLineFlat(
            Color32[] pixels,
            int width,
            int height,
            bool horizontal,
            int fixedPos,
            int length,
            Color32? baseColor,
            out Color32 lineColor)
        {
            int count = Mathf.Min(SampleCount, length);
            if (count < 2) count = 2;
            float step = (length - 1) / (float)(count - 1);

            Color32 first = default;
            bool hasFirst = false;
            int match = 0;

            for (int i = 0; i < count; i++)
            {
                int pos = Mathf.RoundToInt(i * step);
                int x = horizontal ? pos : fixedPos;
                int y = horizontal ? fixedPos : pos;
                Color32 c = GetPixel(pixels, width, height, x, y);
                if (!hasFirst)
                {
                    first = c;
                    hasFirst = true;
                }
                if (ColorDistance(c, first) <= Tolerance) match++;
            }

            lineColor = first;
            if (!hasFirst) return false;
            if ((match / (float)count) < FlatRatio) return false;
            if (baseColor.HasValue && ColorDistance(first, baseColor.Value) > Tolerance) return false;
            return true;
        }

        private static Color32 GetPixel(Color32[] pixels, int width, int height, int x, int y)
        {
            if (x < 0) x = 0;
            if (x >= width) x = width - 1;
            if (y < 0) y = 0;
            if (y >= height) y = height - 1;
            return pixels[y * width + x];
        }

        private static int ColorDistance(Color32 a, Color32 b)
        {
            return Mathf.Abs(a.r - b.r) +
                   Mathf.Abs(a.g - b.g) +
                   Mathf.Abs(a.b - b.b) +
                   Mathf.Abs(a.a - b.a);
        }
    }
}
