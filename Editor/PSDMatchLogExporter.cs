using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PSDImporter
{
    /// <summary>
    /// Exports restore-mode match results to a JSON file under Assets/PSDTools/Log/.
    /// Each PSD layer records: its geometry, all scored candidates (Top 10),
    /// and the final matched node with full score breakdown + coordinates.
    /// Only runs when showDetailedLog is enabled.
    /// </summary>
    public static class PSDMatchLogExporter
    {
        // ── JSON DTOs ───────────────────────────────────────────────────

        [Serializable]
        private class MatchLogRoot
        {
            public string timestamp;
            public string psdName;
            public int psdWidth;
            public int psdHeight;
            public MatchConfigLog config;
            public List<LayerLog> layers = new List<LayerLog>();
            public SummaryLog summary;
        }

        [Serializable]
        private class MatchConfigLog
        {
            public float maxDistanceError;
            public float maxSizeDiff;
            public float weightPosition;
            public float weightSize;
            public float weightType;
            public bool skipInactiveMatch;
            public bool allowUnmatched;
            public float unmatchedPenalty;
            public float minAcceptScore;
            public bool preLockEnabled;
            public float preLockThreshold;
        }

        [Serializable]
        private class LayerLog
        {
            public string pngName;
            public int id;
            public string uiType;
            public GeometryLog psdGeometry;
            public List<CandidateLog> candidates = new List<CandidateLog>();
            public FinalMatchLog finalMatch;
        }

        [Serializable]
        private class GeometryLog
        {
            public float centerX;
            public float centerY;
            public float width;
            public float height;
            public float rectMinX;
            public float rectMinY;
            public float rectMaxX;
            public float rectMaxY;
            public int depth;
        }

        [Serializable]
        private class CandidateLog
        {
            public string nodePath;
            public bool active;
            public GeometryLog nodeGeometry;
            public float distance;
            public float diffW;
            public float diffH;
            public float scorePos;
            public float scoreSize;
            public float scoreType;
            public float weightedPos;
            public float weightedSize;
            public float weightedType;
            public float totalScore;
        }

        [Serializable]
        private class FinalMatchLog
        {
            public string matchMethod;   // "ID_History" | "PreLock" | "Hungarian" | "Unmatched"
            public string nodePath;
            public float score;
            public string statusInfo;
            public GeometryLog nodeGeometry;  // null if unmatched
        }

        [Serializable]
        private class SummaryLog
        {
            public int totalLayers;
            public int matched;
            public int unmatched;
            public float unmatchedRate;
        }

        // ── Public API ──────────────────────────────────────────────────

        /// <summary>
        /// Collect match data from a completed RunAutoMatch and write JSON.
        /// Call this AFTER RunAutoMatch has finished so all binding fields are populated.
        /// </summary>
        public static void Export(
            List<BindingPairViewModel> bindings,
            GameObject targetRoot,
            PSDData cachedPsdData,
            PSDImportConfig config)
        {
            if (bindings == null || bindings.Count == 0 || config == null)
                return;

            RectTransform rootRect = targetRoot != null ? targetRoot.transform as RectTransform : null;
            if (rootRect == null)
                return;

            var root = BuildLogRoot(cachedPsdData, config);

            int matched = 0;
            int unmatched = 0;

            for (int i = 0; i < bindings.Count; i++)
            {
                var bind = bindings[i];
                var layer = BuildLayerLog(bind, rootRect, cachedPsdData, config);
                root.layers.Add(layer);

                if (bind.unityNode != null)
                    matched++;
                else
                    unmatched++;
            }

            root.summary = new SummaryLog
            {
                totalLayers = bindings.Count,
                matched = matched,
                unmatched = unmatched,
                unmatchedRate = bindings.Count > 0 ? (float)unmatched / bindings.Count : 0f
            };

            WriteJsonFile(root, cachedPsdData);
        }

        // ── Internal helpers ────────────────────────────────────────────

        private static string ExtractPsdName(PSDData psdData)
        {
            if (psdData == null) return "unknown";
            // Try to get a meaningful name from psdAssetsFolder
            // e.g. "Assets/Images/MyUI" → "MyUI"
            if (!string.IsNullOrEmpty(psdData.psdAssetsFolder))
            {
                string folder = psdData.psdAssetsFolder.Replace("\\", "/").TrimEnd('/');
                int lastSlash = folder.LastIndexOf('/');
                if (lastSlash >= 0 && lastSlash < folder.Length - 1)
                    return folder.Substring(lastSlash + 1);
                return folder;
            }
            return "unknown";
        }

        private static MatchLogRoot BuildLogRoot(PSDData psdData, PSDImportConfig config)
        {
            return new MatchLogRoot
            {
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                psdName = ExtractPsdName(psdData),
                psdWidth = psdData != null ? psdData.width : 0,
                psdHeight = psdData != null ? psdData.height : 0,
                config = new MatchConfigLog
                {
                    maxDistanceError = config.maxDistanceError,
                    maxSizeDiff = config.maxSizeDiff,
                    weightPosition = config.weightPosition,
                    weightSize = config.weightSize,
                    weightType = config.weightType,
                    skipInactiveMatch = config.skipInactiveMatch,
                    allowUnmatched = config.allowUnmatched,
                    unmatchedPenalty = config.unmatchedPenalty,
                    minAcceptScore = config.minAcceptScore,
                    preLockEnabled = config.preLockEnabled,
                    preLockThreshold = config.preLockThreshold
                }
            };
        }

        private static LayerLog BuildLayerLog(
            BindingPairViewModel bind,
            RectTransform rootRect,
            PSDData cachedPsdData,
            PSDImportConfig config)
        {
            var psdGeom = PSDMatchGeometry.BuildPsdGeom(bind.psdItem, cachedPsdData.width, cachedPsdData.height);

            var layer = new LayerLog
            {
                pngName = bind.psdItem.pngName,
                id = bind.psdItem.id,
                uiType = bind.psdItem.uiType,
                psdGeometry = ToGeometryLog(psdGeom.centerLocal, psdGeom.sizeLocal,
                    psdGeom.rectMinLocal, psdGeom.rectMaxLocal, psdGeom.depth)
            };

            // Collect all candidate nodes from the prefab and score them
            var allNodes = rootRect.GetComponentsInChildren<RectTransform>(true);
            var candidates = new List<CandidateLog>();

            for (int j = 0; j < allNodes.Length; j++)
            {
                var node = allNodes[j];
                if (node == rootRect.transform)
                    continue;

                // Type filter (same logic as VisualBindingRestoreService.IsTypeMatch)
                if (!IsTypeMatchForLog(node, bind.psdItem.uiType))
                    continue;

                // Calculate score using the same pipeline
                var nodeGeom = PSDMatchGeometry.ExtractNodeGeom(node, rootRect);
                bool typeMatch = IsTypeMatchForLog(node, bind.psdItem.uiType);
                var scoreBreakdown = PSDMatchScoring.Evaluate(
                    PSDMatchScoring.ScoringInput.FromConfig(nodeGeom, psdGeom, typeMatch, config));

                if (scoreBreakdown.total > 1f)
                {
                    candidates.Add(new CandidateLog
                    {
                        nodePath = GetTransformPath(node),
                        active = node.gameObject.activeInHierarchy,
                        nodeGeometry = ToGeometryLog(nodeGeom.centerLocal, nodeGeom.sizeLocal,
                            nodeGeom.rectMinLocal, nodeGeom.rectMaxLocal, nodeGeom.depth),
                        distance = scoreBreakdown.geometry.distance,
                        diffW = scoreBreakdown.geometry.diffW,
                        diffH = scoreBreakdown.geometry.diffH,
                        scorePos = scoreBreakdown.scorePos,
                        scoreSize = scoreBreakdown.scoreSize,
                        scoreType = scoreBreakdown.scoreType,
                        weightedPos = scoreBreakdown.weightedPos,
                        weightedSize = scoreBreakdown.weightedSize,
                        weightedType = scoreBreakdown.weightedType,
                        totalScore = scoreBreakdown.total
                    });
                }
            }

            // Sort by score descending, take top 10
            candidates.Sort((a, b) => b.totalScore.CompareTo(a.totalScore));
            int topN = Math.Min(candidates.Count, 10);
            for (int k = 0; k < topN; k++)
            {
                layer.candidates.Add(candidates[k]);
            }

            // Final match result
            if (bind.unityNode != null)
            {
                string method = bind.isIdMatched ? "ID_History"
                    : bind.statusInfo != null && bind.statusInfo.StartsWith("PreLock") ? "PreLock"
                    : "Hungarian";

                var matchNode = bind.unityNode.GetComponent<RectTransform>();
                GeometryLog matchGeom = null;
                if (matchNode != null)
                {
                    var ng = PSDMatchGeometry.ExtractNodeGeom(matchNode, rootRect);
                    matchGeom = ToGeometryLog(ng.centerLocal, ng.sizeLocal,
                        ng.rectMinLocal, ng.rectMaxLocal, ng.depth);
                }

                layer.finalMatch = new FinalMatchLog
                {
                    matchMethod = method,
                    nodePath = GetTransformPath(bind.unityNode),
                    score = bind.score,
                    statusInfo = bind.statusInfo,
                    nodeGeometry = matchGeom
                };
            }
            else
            {
                layer.finalMatch = new FinalMatchLog
                {
                    matchMethod = "Unmatched",
                    nodePath = null,
                    score = 0f,
                    statusInfo = bind.statusInfo,
                    nodeGeometry = null
                };
            }

            return layer;
        }

        private static GeometryLog ToGeometryLog(
            Vector2 center, Vector2 size, Vector2 min, Vector2 max, int depth)
        {
            return new GeometryLog
            {
                centerX = Mathf.Round(center.x * 10f) / 10f,
                centerY = Mathf.Round(center.y * 10f) / 10f,
                width = Mathf.Round(size.x * 10f) / 10f,
                height = Mathf.Round(size.y * 10f) / 10f,
                rectMinX = Mathf.Round(min.x * 10f) / 10f,
                rectMinY = Mathf.Round(min.y * 10f) / 10f,
                rectMaxX = Mathf.Round(max.x * 10f) / 10f,
                rectMaxY = Mathf.Round(max.y * 10f) / 10f,
                depth = depth
            };
        }

        private static string GetTransformPath(Transform t)
        {
            if (t == null) return string.Empty;
            List<string> parts = new List<string>();
            Transform current = t;
            while (current != null)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static bool IsTypeMatchForLog(Transform node, string psdType)
        {
            if (psdType == "Button") return node.GetComponent<Button>() != null;
            if (psdType == "Text") return node.GetComponent<Text>() != null;
            if (psdType == "RawImage") return node.GetComponent<RawImage>() != null;
            if (psdType == "Image") return node.GetComponent<Image>() != null && node.GetComponent<Button>() == null;
            if (psdType == "Layout") return node.GetComponent<LayoutGroup>() != null;
            if (psdType == "Item") return node.GetComponent<LayoutGroup>() == null && node.GetComponentInParent<LayoutGroup>() != null;
            return false;
        }

        private static void WriteJsonFile(MatchLogRoot root, PSDData psdData)
        {
            // Log directory: Assets/PSDTools/Log/
            string logDir = Path.Combine(Application.dataPath, "PSDTools", "Log");
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string psdName = SanitizeFileName(ExtractPsdName(psdData));
            string fileName = $"MatchLog_{psdName}_{timestamp}.json";
            string filePath = Path.Combine(logDir, fileName);

            // Serialize using Newtonsoft JSON (already bundled in project)
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(root, Newtonsoft.Json.Formatting.Indented);

            File.WriteAllText(filePath, json, Encoding.UTF8);

            // Refresh so it appears in the Project window
            AssetDatabase.Refresh();

            Debug.Log($"[MatchLog] Exported match log to Assets/PSDTools/Log/{fileName} ({root.layers.Count} layers, {root.summary.matched} matched, {root.summary.unmatched} unmatched)");
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (Array.IndexOf(invalid, c) >= 0)
                    sb.Append('_');
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
