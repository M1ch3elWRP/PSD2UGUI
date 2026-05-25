using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TZ.Framework.UGUI;
using TZ.UI;

namespace PSDImporter
{
    public enum StdPrefabApplyMode
    {
        None = 0,
        ReuseExisting = 1,
        InstantiatePending = 2
    }

    internal static class PSDStdPrefabSupport
    {
        internal const string StdBtnKind = "StdBtn";
        internal const string StdItemKind = "StdItem";
        internal const string StdPopupKind = "PopUp";
        internal const string StdItemVariantBox = "Box";
        internal const string StdItemVariantCircle = "Circle";
        internal const string DefaultStdButtonPrefabFolder = "Assets/ArtWorks/UI/Resources/Mini/UIPrefabs/CommonPrefbs/Btn/NormalBtn";
        internal const string DefaultStdItemPrefabFolder = "Assets/ArtWorks/UI/Resources/Mini/UIPrefabs/CommonPrefbs/ItemIconNew";
        internal const string DefaultStdPopupPrefabFolder = "Assets/ArtWorks/UI/Resources/Mini/UIPrefabs/CommonPrefbs/Panel";

        private const string StdItemBoxPrefabName = "UIItemIcon_Box";
        private const string StdItemCirclePrefabName = "UIItemIcon_Circle";
        private const float DefaultStdItemReuseMinScore = 100f;
        private const float DefaultStdItemReuseMinGap = 10f;

        private static readonly string[] ButtonColorPriority =
        {
            "Yellow",
            "Blue",
            "Red",
            "Gray"
        };

        private static readonly StdPopupPrefabInfo[] PopupPrefabInfos =
        {
            new StdPopupPrefabInfo("Pnl_Win00", 1004f, 642f),
            new StdPopupPrefabInfo("Pnl_Win01", 1216f, 710f),
            new StdPopupPrefabInfo("Pnl_Win02", 982f, 640f),
            new StdPopupPrefabInfo("Pnl_Win03", 982f, 640f),
            new StdPopupPrefabInfo("Pnl_Win04", 480f, 590f),
            new StdPopupPrefabInfo("Pnl_Win05", 608f, 396f),
            new StdPopupPrefabInfo("Pnl_Win06", 900.41f, 590f),
            new StdPopupPrefabInfo("Pnl_Win07", 618f, 626f),
            new StdPopupPrefabInfo("Pnl_Win08", 900.41f, 590f),
            new StdPopupPrefabInfo("Pnl_Win09", 1144f, 680f)
        };

        private static readonly Dictionary<string, UIItemPool> GeneratedItemPoolCache = new Dictionary<string, UIItemPool>(StringComparer.OrdinalIgnoreCase);

        private class StdButtonReuseCandidate
        {
            public RectTransform rect;
            public string assetPath;
            public string source;
        }

        private class StdPopupReuseCandidate
        {
            public RectTransform rect;
            public string assetPath;
            public string source;
            public Vector2 visibleSize;
        }

        private struct StdPopupPrefabInfo
        {
            public readonly string prefabName;
            public readonly Vector2 visibleSize;

            public StdPopupPrefabInfo(string prefabName, float visibleWidth, float visibleHeight)
            {
                this.prefabName = prefabName;
                visibleSize = new Vector2(visibleWidth, visibleHeight);
            }
        }

        internal static void ResetCreateSessionState()
        {
            GeneratedItemPoolCache.Clear();
        }

        internal static bool HasAnyStdPrefabMatchEnabled(PSDImportConfig config)
        {
            return config != null && (config.enableStdButtonMatch || config.enableStdItemMatch || config.enableStdPopupMatch);
        }

        internal static bool IsStdPrefabRoot(PicData item)
        {
            return item.isStdPrefabRoot && !string.IsNullOrEmpty(item.stdPrefabKind);
        }

        internal static bool IsStdButton(PicData item)
        {
            return string.Equals(item.stdPrefabKind, StdBtnKind, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsStdItem(PicData item)
        {
            return string.Equals(item.stdPrefabKind, StdItemKind, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsStdPopup(PicData item)
        {
            return string.Equals(item.stdPrefabKind, StdPopupKind, StringComparison.OrdinalIgnoreCase);
        }

        internal static string DetectStdPrefabKind(string rawName)
        {
            if (string.IsNullOrEmpty(rawName))
            {
                return string.Empty;
            }

            if (PSDTagUtility.HasTag(rawName, "@StdBtn"))
            {
                return StdBtnKind;
            }

            if (PSDTagUtility.HasTag(rawName, "@PopUp"))
            {
                return StdPopupKind;
            }

            if (PSDTagUtility.HasAnyTag(rawName, "@ItemBox", "@ItemCircle"))
            {
                return StdItemKind;
            }

            return string.Empty;
        }

        internal static string DetectStdPrefabVariant(string rawName)
        {
            if (string.IsNullOrEmpty(rawName))
            {
                return string.Empty;
            }

            if (PSDTagUtility.HasTag(rawName, "@ItemBox"))
            {
                return StdItemVariantBox;
            }

            if (PSDTagUtility.HasTag(rawName, "@ItemCircle"))
            {
                return StdItemVariantCircle;
            }

            return string.Empty;
        }

        internal static string StripStdPrefabTags(string rawName)
        {
            if (string.IsNullOrEmpty(rawName))
            {
                return rawName;
            }

            return PSDTagUtility.RemoveTags(rawName, "@StdBtn", "@PopUp", "@ItemBox", "@ItemCircle");
        }

        internal static bool IsReservedStdPrefabNode(RectTransform node, PSDImportConfig config)
        {
            return node != null &&
                   config != null &&
                   TryGetReservedStdPrefabAssetPath(node.gameObject, config, out _);
        }

        internal static bool IsInsideReservedStdPrefab(RectTransform node, PSDImportConfig config)
        {
            if (node == null || !HasAnyStdPrefabMatchEnabled(config))
            {
                return false;
            }

            Transform cursor = node;
            while (cursor != null)
            {
                if (cursor is RectTransform rect && IsReservedStdPrefabNode(rect, config))
                {
                    return true;
                }

                cursor = cursor.parent;
            }

            return false;
        }

        internal static void ResolveStdPrefabBindings(
            List<BindingPairViewModel> bindings,
            GameObject targetRoot,
            PSDData cachedPsdData,
            PSDImportConfig config,
            HashSet<Transform> occupiedNodes,
            HashSet<BindingPairViewModel> matchedBindings,
            Dictionary<int, Transform> psdIdToMatchedNode,
            bool logDetail)
        {
            if (bindings == null ||
                bindings.Count == 0 ||
                targetRoot == null ||
                cachedPsdData == null ||
                config == null ||
                !HasAnyStdPrefabMatchEnabled(config))
            {
                return;
            }

            RectTransform rootRect = targetRoot.transform as RectTransform;
            if (rootRect == null)
            {
                return;
            }

            string stdButtonFolder = GetStdButtonPrefabFolder(config);
            string stdItemFolder = GetStdItemPrefabFolder(config);
            string stdPopupFolder = GetStdPopupPrefabFolder(config);
            List<StdButtonReuseCandidate> stdButtonNodes = config.enableStdButtonMatch
                ? FindStdButtonInstanceRoots(targetRoot, config, occupiedNodes)
                : new List<StdButtonReuseCandidate>();
            List<StdPopupReuseCandidate> stdPopupNodes = config.enableStdPopupMatch
                ? FindStdPopupInstanceRoots(targetRoot, config, occupiedNodes)
                : new List<StdPopupReuseCandidate>();
            string preferredColor = config.enableStdButtonMatch
                ? GetDominantStdButtonColor(targetRoot, stdButtonFolder)
                : null;

            foreach (BindingPairViewModel bind in bindings)
            {
                if (bind == null || !IsStdPrefabRoot(bind.psdItem))
                {
                    continue;
                }

                if (IsStdButton(bind.psdItem))
                {
                    if (config.enableStdButtonMatch)
                    {
                        ResolveStdButtonBinding(
                            bind,
                            rootRect,
                            cachedPsdData,
                            config,
                            occupiedNodes,
                            matchedBindings,
                            psdIdToMatchedNode,
                            logDetail,
                            stdButtonNodes,
                            stdButtonFolder,
                            preferredColor);
                    }

                    continue;
                }

                if (IsStdPopup(bind.psdItem))
                {
                    if (config.enableStdPopupMatch)
                    {
                        ResolveStdPopupBinding(
                            bind,
                            rootRect,
                            cachedPsdData,
                            config,
                            occupiedNodes,
                            matchedBindings,
                            psdIdToMatchedNode,
                            logDetail,
                            stdPopupNodes,
                            stdPopupFolder);
                    }

                    continue;
                }

                if (IsStdItem(bind.psdItem))
                {
                    if (config.enableStdItemMatch)
                    {
                        ResolveStdItemBinding(
                            bind,
                            targetRoot,
                            rootRect,
                            cachedPsdData,
                            config,
                            occupiedNodes,
                            matchedBindings,
                            psdIdToMatchedNode,
                            logDetail,
                            stdItemFolder);
                    }

                    continue;
                }
            }
        }

        internal static GameObject InstantiatePendingPrefab(BindingPairViewModel bind, Transform parent)
        {
            if (bind == null || string.IsNullOrEmpty(bind.stdPrefabAssetPath))
            {
                return null;
            }

            GameObject sceneInstance = InstantiateFromScenePoolOrTemplate(bind.stdPrefabAssetPath, parent);
            if (sceneInstance != null)
            {
                if (!string.IsNullOrEmpty(bind.psdItem.cleanName))
                {
                    sceneInstance.name = bind.psdItem.cleanName;
                }

                return sceneInstance;
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(bind.stdPrefabAssetPath);
            if (prefab == null)
            {
                return null;
            }

            GameObject fallbackInstance = PrefabUtility.InstantiatePrefab(prefab, parent) as GameObject;
            if (fallbackInstance != null)
            {
                Debug.Log($"[StdPrefab] Instantiate pending via asset prefab parent={GetTransformPath(parent)} asset={bind.stdPrefabAssetPath}");
            }

            return fallbackInstance;
        }

        internal static GameObject InstantiatePrefabForCreate(PicData item, PSDImportConfig config, Transform parent, out string assetPath)
        {
            assetPath = ResolvePrefabAssetForCreate(item, config, out _);
            if (string.IsNullOrEmpty(assetPath))
            {
                return null;
            }

            GameObject sceneInstance = InstantiateFromScenePoolOrTemplate(assetPath, parent);
            if (sceneInstance != null)
            {
                if (!string.IsNullOrEmpty(item.cleanName))
                {
                    sceneInstance.name = item.cleanName;
                }

                return sceneInstance;
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                return null;
            }

            GameObject instance = PrefabUtility.InstantiatePrefab(prefab, parent) as GameObject;
            if (instance != null && !string.IsNullOrEmpty(item.cleanName))
            {
                instance.name = item.cleanName;
            }

            if (instance != null)
            {
                Debug.Log($"[StdPrefab] Instantiate via asset prefab parent={GetTransformPath(parent)} asset={assetPath}");
            }

            return instance;
        }

        internal static string ResolvePrefabAssetForCreate(PicData item, PSDImportConfig config, out float score)
        {
            score = float.MinValue;
            if (IsStdButton(item))
            {
                return SelectStdButtonPrefabAsset(item, config, null, out score);
            }

            if (IsStdItem(item))
            {
                return ResolveStdItemPrefabAssetByVariant(config, item.stdPrefabVariant, out score);
            }

            if (IsStdPopup(item))
            {
                return SelectStdPopupPrefabAsset(item, config, out score);
            }

            return null;
        }

        internal static string GetStdButtonPrefabFolder(PSDImportConfig config)
        {
            string folder = config != null ? config.stdButtonPrefabFolder : null;
            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = DefaultStdButtonPrefabFolder;
            }

            return NormalizeAssetFolder(folder);
        }

        internal static string GetStdItemPrefabFolder(PSDImportConfig config)
        {
            string folder = config != null ? config.stdItemPrefabFolder : null;
            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = DefaultStdItemPrefabFolder;
            }

            return NormalizeAssetFolder(folder);
        }

        internal static string GetStdPopupPrefabFolder(PSDImportConfig config)
        {
            string folder = config != null ? config.stdPopupPrefabFolder : null;
            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = DefaultStdPopupPrefabFolder;
            }

            return NormalizeAssetFolder(folder);
        }

        private static void ResolveStdButtonBinding(
            BindingPairViewModel bind,
            RectTransform rootRect,
            PSDData cachedPsdData,
            PSDImportConfig config,
            HashSet<Transform> occupiedNodes,
            HashSet<BindingPairViewModel> matchedBindings,
            Dictionary<int, Transform> psdIdToMatchedNode,
            bool logDetail,
            List<StdButtonReuseCandidate> stdButtonNodes,
            string stdButtonFolder,
            string preferredColor)
        {
            if (bind.stdPrefabCandidates == null)
            {
                bind.stdPrefabCandidates = new List<StdPrefabCandidateViewModel>();
            }
            else
            {
                bind.stdPrefabCandidates.Clear();
            }

            bind.stdPrefabFailureReason = null;
            RectTransform bestNode = null;
            string bestNodeAssetPath = null;
            StdPrefabCandidateViewModel bestCandidateLog = null;
            float bestScore = float.MinValue;
            float secondBestScore = float.MinValue;

            PSDMatchGeometry.PsdGeom psdGeom = PSDMatchGeometry.BuildPsdGeom(bind.psdItem, cachedPsdData.width, cachedPsdData.height);
            Transform matchedParentNode = GetMatchedParentNode(bind.psdItem.parentNodeId, psdIdToMatchedNode);

            for (int i = 0; i < stdButtonNodes.Count; i++)
            {
                StdButtonReuseCandidate candidateInfo = stdButtonNodes[i];
                RectTransform candidate = candidateInfo != null ? candidateInfo.rect : null;
                if (candidate == null || occupiedNodes.Contains(candidate.transform))
                {
                    continue;
                }

                float parentAffinity = GetParentAffinity(candidate, matchedParentNode, config);
                PSDMatchGeometry.NodeGeom nodeGeom = PSDMatchGeometry.ExtractNodeGeom(candidate, rootRect);
                PSDMatchScoring.ScoreBreakdown breakdown = VisualBindingRestoreService.CalculateScoreFromGeometry(
                    nodeGeom,
                    psdGeom,
                    100f,
                    config,
                    parentAffinity);

                float score = breakdown.total;
                StdPrefabCandidateViewModel candidateLog = new StdPrefabCandidateViewModel
                {
                    nodePath = GetTransformPath(candidate),
                    assetPath = candidateInfo.assetPath,
                    source = candidateInfo.source,
                    score = score,
                    distance = breakdown.geometry.distance,
                    diffW = breakdown.geometry.diffW,
                    diffH = breakdown.geometry.diffH,
                    accepted = false
                };
                bind.stdPrefabCandidates.Add(candidateLog);

                if (score > bestScore)
                {
                    secondBestScore = bestScore;
                    bestScore = score;
                    bestNode = candidate;
                    bestNodeAssetPath = candidateInfo.assetPath;
                    bestCandidateLog = candidateLog;
                }
                else if (score > secondBestScore)
                {
                    secondBestScore = score;
                }
            }

            bool hasSecondBest = secondBestScore > float.MinValue * 0.5f;
            float scoreGap = hasSecondBest ? bestScore - secondBestScore : (bestNode != null ? bestScore : 0f);
            bind.bestCandidateScore = bestNode != null ? bestScore : 0f;
            bind.secondBestCandidateScore = hasSecondBest ? secondBestScore : 0f;
            bind.scoreMargin = scoreGap;

            string reuseFailure = null;
            if (bestNode == null)
            {
                reuseFailure = "reuse failed: no candidate";
            }
            else if (bestScore < config.stdButtonReuseMinScore)
            {
                reuseFailure = $"reuse failed: bestScore {bestScore:F1} < min {config.stdButtonReuseMinScore:F1}";
            }
            else if (hasSecondBest && scoreGap < config.stdButtonReuseMinGap)
            {
                reuseFailure = $"reuse failed: gap {scoreGap:F1} < min {config.stdButtonReuseMinGap:F1}";
            }

            bool canReuseExisting = string.IsNullOrEmpty(reuseFailure);
            if (bestCandidateLog != null)
            {
                bestCandidateLog.secondBestScore = bind.secondBestCandidateScore;
                bestCandidateLog.scoreGap = scoreGap;
                bestCandidateLog.accepted = canReuseExisting;
                bestCandidateLog.rejectReason = canReuseExisting ? null : reuseFailure;
            }

            for (int i = 0; i < bind.stdPrefabCandidates.Count; i++)
            {
                StdPrefabCandidateViewModel candidate = bind.stdPrefabCandidates[i];
                if (candidate != bestCandidateLog && string.IsNullOrEmpty(candidate.rejectReason))
                {
                    candidate.rejectReason = "lower score";
                }
            }

            bind.stdPrefabCandidates = bind.stdPrefabCandidates
                .OrderByDescending(c => c.score)
                .Take(10)
                .ToList();

            if (canReuseExisting)
            {
                bind.unityNode = bestNode;
                bind.score = bestScore;
                bind.isConfirmed = true;
                bind.isLowConfidence = false;
                bind.stdPrefabFailureReason = null;
                bind.stdPrefabMode = StdPrefabApplyMode.ReuseExisting;
                bind.stdPrefabAssetPath = bestNodeAssetPath;
                bind.statusInfo = $"StdBtn reuse: {bestNode.name} ({bestScore:F0})";

                matchedBindings.Add(bind);
                occupiedNodes.Add(bestNode.transform);
                psdIdToMatchedNode[bind.psdItem.id] = bestNode.transform;

                if (logDetail)
                {
                    Debug.Log($"[StdPrefab] Reuse StdBtn {bind.psdItem.pngName} -> {GetTransformPath(bestNode)} score={bestScore:F1}");
                }

                return;
            }

            string prefabAssetPath = SelectStdButtonPrefabAsset(bind.psdItem, config, preferredColor, out float prefabScore);
            if (!string.IsNullOrEmpty(prefabAssetPath))
            {
                bind.unityNode = null;
                bind.score = 0f;
                bind.isConfirmed = false;
                bind.isLowConfidence = bestNode != null;
                bind.stdPrefabMode = StdPrefabApplyMode.InstantiatePending;
                bind.stdPrefabAssetPath = prefabAssetPath;
                bind.stdPrefabFailureReason = reuseFailure;
                bind.statusInfo = $"StdBtn instantiate: {Path.GetFileNameWithoutExtension(prefabAssetPath)}; {reuseFailure}";

                if (logDetail)
                {
                    Debug.Log($"[StdPrefab] Instantiate StdBtn {bind.psdItem.pngName} -> {prefabAssetPath} prefabScore={prefabScore:F1}; {reuseFailure}");
                }
            }
            else
            {
                bind.stdPrefabMode = StdPrefabApplyMode.None;
                bind.stdPrefabAssetPath = null;
                bind.stdPrefabFailureReason = string.IsNullOrEmpty(reuseFailure) ? "prefab not found" : $"{reuseFailure}; prefab not found";
                bind.statusInfo = $"StdBtn prefab not found; {bind.stdPrefabFailureReason}";
            }
        }

        private static void ResolveStdPopupBinding(
            BindingPairViewModel bind,
            RectTransform rootRect,
            PSDData cachedPsdData,
            PSDImportConfig config,
            HashSet<Transform> occupiedNodes,
            HashSet<BindingPairViewModel> matchedBindings,
            Dictionary<int, Transform> psdIdToMatchedNode,
            bool logDetail,
            List<StdPopupReuseCandidate> stdPopupNodes,
            string stdPopupFolder)
        {
            if (bind.stdPrefabCandidates == null)
            {
                bind.stdPrefabCandidates = new List<StdPrefabCandidateViewModel>();
            }
            else
            {
                bind.stdPrefabCandidates.Clear();
            }

            bind.stdPrefabFailureReason = null;
            RectTransform bestNode = null;
            string bestNodeAssetPath = null;
            StdPrefabCandidateViewModel bestCandidateLog = null;
            float bestScore = float.MinValue;
            float secondBestScore = float.MinValue;

            PSDMatchGeometry.PsdGeom psdGeom = PSDMatchGeometry.BuildPsdGeom(bind.psdItem, cachedPsdData.width, cachedPsdData.height);
            Transform matchedParentNode = GetMatchedParentNode(bind.psdItem.parentNodeId, psdIdToMatchedNode);

            for (int i = 0; i < stdPopupNodes.Count; i++)
            {
                StdPopupReuseCandidate candidateInfo = stdPopupNodes[i];
                RectTransform candidate = candidateInfo != null ? candidateInfo.rect : null;
                if (candidate == null || occupiedNodes.Contains(candidate.transform))
                {
                    continue;
                }

                float parentAffinity = GetParentAffinity(candidate, matchedParentNode, config);
                PSDMatchGeometry.NodeGeom nodeGeom = BuildStdPopupNodeGeom(candidate, rootRect, candidateInfo.visibleSize);
                PSDMatchScoring.ScoreBreakdown breakdown = VisualBindingRestoreService.CalculateScoreFromGeometry(
                    nodeGeom,
                    psdGeom,
                    100f,
                    config,
                    parentAffinity);

                float score = breakdown.total;
                StdPrefabCandidateViewModel candidateLog = new StdPrefabCandidateViewModel
                {
                    nodePath = GetTransformPath(candidate),
                    assetPath = candidateInfo.assetPath,
                    source = candidateInfo.source,
                    score = score,
                    distance = breakdown.geometry.distance,
                    diffW = breakdown.geometry.diffW,
                    diffH = breakdown.geometry.diffH,
                    accepted = false
                };
                bind.stdPrefabCandidates.Add(candidateLog);

                if (score > bestScore)
                {
                    secondBestScore = bestScore;
                    bestScore = score;
                    bestNode = candidate;
                    bestNodeAssetPath = candidateInfo.assetPath;
                    bestCandidateLog = candidateLog;
                }
                else if (score > secondBestScore)
                {
                    secondBestScore = score;
                }
            }

            bool hasSecondBest = secondBestScore > float.MinValue * 0.5f;
            float scoreGap = hasSecondBest ? bestScore - secondBestScore : (bestNode != null ? bestScore : 0f);
            bind.bestCandidateScore = bestNode != null ? bestScore : 0f;
            bind.secondBestCandidateScore = hasSecondBest ? secondBestScore : 0f;
            bind.scoreMargin = scoreGap;

            string reuseFailure = null;
            if (bestNode == null)
            {
                reuseFailure = "reuse failed: no candidate";
            }
            else if (bestScore < config.stdPopupReuseMinScore)
            {
                reuseFailure = $"reuse failed: bestScore {bestScore:F1} < min {config.stdPopupReuseMinScore:F1}";
            }
            else if (hasSecondBest && scoreGap < config.stdPopupReuseMinGap)
            {
                reuseFailure = $"reuse failed: gap {scoreGap:F1} < min {config.stdPopupReuseMinGap:F1}";
            }

            bool canReuseExisting = string.IsNullOrEmpty(reuseFailure);
            if (bestCandidateLog != null)
            {
                bestCandidateLog.secondBestScore = bind.secondBestCandidateScore;
                bestCandidateLog.scoreGap = scoreGap;
                bestCandidateLog.accepted = canReuseExisting;
                bestCandidateLog.rejectReason = canReuseExisting ? null : reuseFailure;
            }

            for (int i = 0; i < bind.stdPrefabCandidates.Count; i++)
            {
                StdPrefabCandidateViewModel candidate = bind.stdPrefabCandidates[i];
                if (candidate != bestCandidateLog && string.IsNullOrEmpty(candidate.rejectReason))
                {
                    candidate.rejectReason = "lower score";
                }
            }

            bind.stdPrefabCandidates = bind.stdPrefabCandidates
                .OrderByDescending(c => c.score)
                .Take(10)
                .ToList();

            if (canReuseExisting)
            {
                bind.unityNode = bestNode;
                bind.score = bestScore;
                bind.isConfirmed = true;
                bind.isLowConfidence = false;
                bind.stdPrefabFailureReason = null;
                bind.stdPrefabMode = StdPrefabApplyMode.ReuseExisting;
                bind.stdPrefabAssetPath = bestNodeAssetPath;
                bind.statusInfo = $"PopUp reuse: {bestNode.name} ({bestScore:F0})";

                matchedBindings.Add(bind);
                occupiedNodes.Add(bestNode.transform);
                psdIdToMatchedNode[bind.psdItem.id] = bestNode.transform;

                if (logDetail)
                {
                    Debug.Log($"[StdPrefab] Reuse PopUp {bind.psdItem.pngName} -> {GetTransformPath(bestNode)} score={bestScore:F1}");
                }

                return;
            }

            string prefabAssetPath = SelectStdPopupPrefabAsset(bind.psdItem, config, out float prefabScore);
            if (!string.IsNullOrEmpty(prefabAssetPath))
            {
                bind.unityNode = null;
                bind.score = 0f;
                bind.isConfirmed = false;
                bind.isLowConfidence = bestNode != null;
                bind.stdPrefabMode = StdPrefabApplyMode.InstantiatePending;
                bind.stdPrefabAssetPath = prefabAssetPath;
                bind.stdPrefabFailureReason = reuseFailure;
                bind.statusInfo = $"PopUp instantiate: {Path.GetFileNameWithoutExtension(prefabAssetPath)}; {reuseFailure}";

                if (logDetail)
                {
                    Debug.Log($"[StdPrefab] Instantiate PopUp {bind.psdItem.pngName} -> {prefabAssetPath} prefabScore={prefabScore:F1}; {reuseFailure}");
                }
            }
            else
            {
                bind.stdPrefabMode = StdPrefabApplyMode.None;
                bind.stdPrefabAssetPath = null;
                bind.stdPrefabFailureReason = string.IsNullOrEmpty(reuseFailure) ? "prefab not found" : $"{reuseFailure}; prefab not found";
                bind.statusInfo = $"PopUp prefab not found; {bind.stdPrefabFailureReason}";
            }
        }

        private static void ResolveStdItemBinding(
            BindingPairViewModel bind,
            GameObject targetRoot,
            RectTransform rootRect,
            PSDData cachedPsdData,
            PSDImportConfig config,
            HashSet<Transform> occupiedNodes,
            HashSet<BindingPairViewModel> matchedBindings,
            Dictionary<int, Transform> psdIdToMatchedNode,
            bool logDetail,
            string stdItemFolder)
        {
            string variant = NormalizeStdItemVariant(bind.psdItem.stdPrefabVariant);
            if (string.IsNullOrEmpty(variant))
            {
                bind.stdPrefabMode = StdPrefabApplyMode.None;
                bind.stdPrefabAssetPath = null;
                bind.statusInfo = "StdItem variant missing";
                return;
            }

            List<RectTransform> stdItemNodes = FindStdItemInstanceRoots(targetRoot, config, occupiedNodes, variant);
            RectTransform bestNode = null;
            string bestNodeAssetPath = null;
            float bestScore = float.MinValue;
            float secondBestScore = float.MinValue;

            PSDMatchGeometry.PsdGeom psdGeom = PSDMatchGeometry.BuildPsdGeom(bind.psdItem, cachedPsdData.width, cachedPsdData.height);
            Transform matchedParentNode = GetMatchedParentNode(bind.psdItem.parentNodeId, psdIdToMatchedNode);

            for (int i = 0; i < stdItemNodes.Count; i++)
            {
                RectTransform candidate = stdItemNodes[i];
                if (candidate == null || occupiedNodes.Contains(candidate.transform))
                {
                    continue;
                }

                float parentAffinity = GetParentAffinity(candidate, matchedParentNode, config);
                PSDMatchGeometry.NodeGeom nodeGeom = PSDMatchGeometry.ExtractNodeGeom(candidate, rootRect);
                PSDMatchScoring.ScoreBreakdown breakdown = VisualBindingRestoreService.CalculateScoreFromGeometry(
                    nodeGeom,
                    psdGeom,
                    100f,
                    config,
                    parentAffinity);

                float score = breakdown.total;
                if (score > bestScore)
                {
                    secondBestScore = bestScore;
                    bestScore = score;
                    bestNode = candidate;
                    GetStdItemAssetPath(candidate.gameObject, stdItemFolder, variant, out bestNodeAssetPath);
                }
                else if (score > secondBestScore)
                {
                    secondBestScore = score;
                }
            }

            bool canReuseExisting = bestNode != null &&
                                    bestScore >= DefaultStdItemReuseMinScore &&
                                    (secondBestScore <= float.MinValue * 0.5f ||
                                     (bestScore - secondBestScore) >= DefaultStdItemReuseMinGap);

            if (canReuseExisting)
            {
                bind.unityNode = bestNode;
                bind.score = bestScore;
                bind.isConfirmed = true;
                bind.stdPrefabMode = StdPrefabApplyMode.ReuseExisting;
                bind.stdPrefabAssetPath = bestNodeAssetPath;
                bind.statusInfo = $"StdItem reuse: {bestNode.name} ({bestScore:F0})";

                matchedBindings.Add(bind);
                occupiedNodes.Add(bestNode.transform);
                psdIdToMatchedNode[bind.psdItem.id] = bestNode.transform;

                if (logDetail)
                {
                    Debug.Log($"[StdPrefab] Reuse StdItem {bind.psdItem.pngName} ({variant}) -> {GetTransformPath(bestNode)} score={bestScore:F1}");
                }

                return;
            }

            string prefabAssetPath = ResolveStdItemPrefabAssetByVariant(config, variant, out float prefabScore);
            if (!string.IsNullOrEmpty(prefabAssetPath))
            {
                bind.unityNode = null;
                bind.score = 0f;
                bind.isConfirmed = false;
                bind.stdPrefabMode = StdPrefabApplyMode.InstantiatePending;
                bind.stdPrefabAssetPath = prefabAssetPath;
                bind.statusInfo = $"StdItem instantiate: {Path.GetFileNameWithoutExtension(prefabAssetPath)}";

                if (logDetail)
                {
                    Debug.Log($"[StdPrefab] Instantiate StdItem {bind.psdItem.pngName} ({variant}) -> {prefabAssetPath} prefabScore={prefabScore:F1}");
                }
            }
            else
            {
                bind.stdPrefabMode = StdPrefabApplyMode.None;
                bind.stdPrefabAssetPath = null;
                bind.statusInfo = $"StdItem prefab not found ({variant})";
            }
        }

        private static Transform GetMatchedParentNode(int parentNodeId, Dictionary<int, Transform> psdIdToMatchedNode)
        {
            if (parentNodeId <= 0 || psdIdToMatchedNode == null)
            {
                return null;
            }

            psdIdToMatchedNode.TryGetValue(parentNodeId, out Transform matchedParentNode);
            return matchedParentNode;
        }

        private static float GetParentAffinity(RectTransform candidate, Transform matchedParentNode, PSDImportConfig config)
        {
            return matchedParentNode != null && candidate.parent == matchedParentNode
                ? config.parentAffinityBonus
                : 1f;
        }

        private static bool TryGetReservedStdPrefabAssetPath(GameObject gameObject, PSDImportConfig config, out string assetPath)
        {
            assetPath = null;
            if (gameObject == null || config == null)
            {
                return false;
            }

            if (config.enableStdButtonMatch &&
                GetStdButtonAssetPath(gameObject, GetStdButtonPrefabFolder(config), out assetPath))
            {
                return true;
            }

            if (config.enableStdItemMatch &&
                GetStdItemAssetPath(gameObject, GetStdItemPrefabFolder(config), null, out assetPath))
            {
                return true;
            }

            if (config.enableStdPopupMatch &&
                GetStdPopupAssetPath(gameObject, GetStdPopupPrefabFolder(config), out assetPath))
            {
                return true;
            }

            return false;
        }

        private static List<StdButtonReuseCandidate> FindStdButtonInstanceRoots(
            GameObject targetRoot,
            PSDImportConfig config,
            HashSet<Transform> occupiedNodes)
        {
            List<StdButtonReuseCandidate> result = new List<StdButtonReuseCandidate>();
            if (targetRoot == null)
            {
                return result;
            }

            string folder = GetStdButtonPrefabFolder(config);
            HashSet<RectTransform> seen = new HashSet<RectTransform>();
            RectTransform[] allRects = targetRoot.GetComponentsInChildren<RectTransform>(true);
            for (int i = 0; i < allRects.Length; i++)
            {
                RectTransform rect = allRects[i];
                if (rect == null || rect.transform == targetRoot.transform || seen.Contains(rect))
                {
                    continue;
                }

                if (PSDMatchNodeFilter.ShouldSkipInactiveMatch(rect, targetRoot.transform, config))
                {
                    continue;
                }

                if (occupiedNodes != null && occupiedNodes.Contains(rect.transform))
                {
                    continue;
                }

                if (!TryCreateStdButtonReuseCandidate(rect, folder, out StdButtonReuseCandidate candidate))
                {
                    continue;
                }

                if (HasAncestorStdButtonReuseCandidate(rect, targetRoot.transform, folder))
                {
                    continue;
                }

                seen.Add(rect);
                result.Add(candidate);
            }

            return result;
        }

        private static bool HasAncestorStdButtonReuseCandidate(RectTransform rect, Transform stopRoot, string folder)
        {
            Transform cursor = rect != null ? rect.parent : null;
            while (cursor != null && cursor != stopRoot)
            {
                if (cursor is RectTransform ancestor &&
                    TryCreateStdButtonReuseCandidate(ancestor, folder, out _))
                {
                    return true;
                }

                cursor = cursor.parent;
            }

            return false;
        }

        private static bool TryCreateStdButtonReuseCandidate(RectTransform rect, string folder, out StdButtonReuseCandidate candidate)
        {
            candidate = null;
            if (rect == null)
            {
                return false;
            }

            string assetPath;
            if (GetStdButtonAssetPath(rect.gameObject, folder, out assetPath))
            {
                candidate = new StdButtonReuseCandidate
                {
                    rect = rect,
                    assetPath = assetPath,
                    source = "NormalBtnFolder"
                };
                return true;
            }

            bool hasStdButton = rect.GetComponent<UIStdButton>() != null;
            bool isPrefabRoot = IsPrefabInstanceRoot(rect.gameObject);
            bool hasPrefabPath = TryGetPrefabInstanceAssetPath(rect.gameObject, out assetPath);
            bool commonButtonPrefab = hasPrefabPath && IsCommonButtonAssetPath(assetPath);

            if (hasStdButton)
            {
                candidate = new StdButtonReuseCandidate
                {
                    rect = rect,
                    assetPath = hasPrefabPath ? assetPath : null,
                    source = isPrefabRoot
                        ? (commonButtonPrefab ? "CommonBtnPrefabRoot" : "UIStdButtonPrefabRoot")
                        : "UIStdButton"
                };
                return true;
            }

            if (isPrefabRoot && commonButtonPrefab)
            {
                candidate = new StdButtonReuseCandidate
                {
                    rect = rect,
                    assetPath = assetPath,
                    source = "CommonBtnPrefabRoot"
                };
                return true;
            }

            return false;
        }

        private static List<RectTransform> FindStdItemInstanceRoots(
            GameObject targetRoot,
            PSDImportConfig config,
            HashSet<Transform> occupiedNodes,
            string variant)
        {
            List<RectTransform> result = new List<RectTransform>();
            if (targetRoot == null)
            {
                return result;
            }

            string folder = GetStdItemPrefabFolder(config);
            RectTransform[] allRects = targetRoot.GetComponentsInChildren<RectTransform>(true);
            for (int i = 0; i < allRects.Length; i++)
            {
                RectTransform rect = allRects[i];
                if (rect == null || rect.transform == targetRoot.transform)
                {
                    continue;
                }

                if (PSDMatchNodeFilter.ShouldSkipInactiveMatch(rect, targetRoot.transform, config))
                {
                    continue;
                }

                if (occupiedNodes != null && occupiedNodes.Contains(rect.transform))
                {
                    continue;
                }

                if (GetStdItemAssetPath(rect.gameObject, folder, variant, out _))
                {
                    result.Add(rect);
                }
            }

            return result;
        }

        private static List<StdPopupReuseCandidate> FindStdPopupInstanceRoots(
            GameObject targetRoot,
            PSDImportConfig config,
            HashSet<Transform> occupiedNodes)
        {
            List<StdPopupReuseCandidate> result = new List<StdPopupReuseCandidate>();
            if (targetRoot == null)
            {
                return result;
            }

            string folder = GetStdPopupPrefabFolder(config);
            RectTransform[] allRects = targetRoot.GetComponentsInChildren<RectTransform>(true);
            for (int i = 0; i < allRects.Length; i++)
            {
                RectTransform rect = allRects[i];
                if (rect == null || rect.transform == targetRoot.transform)
                {
                    continue;
                }

                if (PSDMatchNodeFilter.ShouldSkipInactiveMatch(rect, targetRoot.transform, config))
                {
                    continue;
                }

                if (occupiedNodes != null && occupiedNodes.Contains(rect.transform))
                {
                    continue;
                }

                if (!GetStdPopupAssetPath(rect.gameObject, folder, out string assetPath))
                {
                    continue;
                }

                if (!TryGetStdPopupVisibleSize(assetPath, out Vector2 visibleSize))
                {
                    continue;
                }

                result.Add(new StdPopupReuseCandidate
                {
                    rect = rect,
                    assetPath = assetPath,
                    source = "CommonPanelPrefabRoot",
                    visibleSize = visibleSize
                });
            }

            return result;
        }

        private static string SelectStdButtonPrefabAsset(
            PicData item,
            PSDImportConfig config,
            string preferredColor,
            out float bestScore)
        {
            bestScore = float.MinValue;
            string bestPath = null;

            foreach (string assetPath in FindStdButtonPrefabAssets(config))
            {
                float score = ScoreStdButtonPrefabAsset(assetPath, item, preferredColor);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPath = assetPath;
                }
            }

            return bestPath;
        }

        private static string ResolveStdItemPrefabAssetByVariant(PSDImportConfig config, string variant, out float score)
        {
            score = 100f;
            string normalizedVariant = NormalizeStdItemVariant(variant);
            string expectedName = GetStdItemPrefabNameByVariant(normalizedVariant);
            if (string.IsNullOrEmpty(expectedName))
            {
                score = float.MinValue;
                return null;
            }

            foreach (string assetPath in FindStdItemPrefabAssets(config))
            {
                string prefabName = Path.GetFileNameWithoutExtension(assetPath);
                if (string.Equals(prefabName, expectedName, StringComparison.OrdinalIgnoreCase))
                {
                    return assetPath;
                }
            }

            score = float.MinValue;
            return null;
        }

        private static string SelectStdPopupPrefabAsset(PicData item, PSDImportConfig config, out float bestScore)
        {
            bestScore = float.MinValue;
            string bestPath = null;

            foreach (string assetPath in FindStdPopupPrefabAssets(config))
            {
                float score = ScoreStdPopupPrefabAsset(assetPath, item);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPath = assetPath;
                }
            }

            return bestPath;
        }

        private static IEnumerable<string> FindStdButtonPrefabAssets(PSDImportConfig config)
        {
            string folder = GetStdButtonPrefabFolder(config);
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder });
            List<string> paths = new List<string>();
            for (int i = 0; i < guids.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (prefab == null || prefab.GetComponent<UIStdButton>() == null)
                {
                    continue;
                }

                paths.Add(assetPath.Replace("\\", "/"));
            }

            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return paths;
        }

        private static IEnumerable<string> FindStdItemPrefabAssets(PSDImportConfig config)
        {
            string folder = GetStdItemPrefabFolder(config);
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder });
            List<string> paths = new List<string>();
            for (int i = 0; i < guids.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]).Replace("\\", "/");
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (prefab == null || prefab.GetComponent<UIPrefabLink>() == null)
                {
                    continue;
                }

                string prefabName = Path.GetFileNameWithoutExtension(assetPath);
                if (!MatchesStdItemPrefabName(prefabName))
                {
                    continue;
                }

                paths.Add(assetPath);
            }

            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return paths;
        }

        private static IEnumerable<string> FindStdPopupPrefabAssets(PSDImportConfig config)
        {
            string folder = GetStdPopupPrefabFolder(config);
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder });
            List<string> paths = new List<string>();
            for (int i = 0; i < guids.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]).Replace("\\", "/");
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (prefab == null)
                {
                    continue;
                }

                string prefabName = Path.GetFileNameWithoutExtension(assetPath);
                if (!MatchesStdPopupPrefabName(prefabName))
                {
                    continue;
                }

                paths.Add(assetPath);
            }

            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return paths;
        }

        private static float ScoreStdButtonPrefabAsset(string assetPath, PicData item, string preferredColor)
        {
            if (!TryGetPrefabRectSize(assetPath, out Vector2 prefabSize))
            {
                return float.MinValue;
            }

            float score = -(Mathf.Abs(prefabSize.x - item.width) + Mathf.Abs(prefabSize.y - item.height));

            string colorKeyword = ExtractButtonColorKeyword(assetPath);
            if (!string.IsNullOrEmpty(preferredColor) &&
                string.Equals(colorKeyword, preferredColor, StringComparison.OrdinalIgnoreCase))
            {
                score += 50f;
            }
            else
            {
                score -= GetColorPriority(colorKeyword) * 0.1f;
            }

            return score;
        }

        private static float ScoreStdPopupPrefabAsset(string assetPath, PicData item)
        {
            if (!TryGetStdPopupVisibleSize(assetPath, out Vector2 visibleSize))
            {
                return float.MinValue;
            }

            return -(Mathf.Abs(visibleSize.x - item.width) + Mathf.Abs(visibleSize.y - item.height));
        }

        private static PSDMatchGeometry.NodeGeom BuildStdPopupNodeGeom(RectTransform candidate, RectTransform rootRect, Vector2 visibleSize)
        {
            PSDMatchGeometry.NodeGeom nodeGeom = PSDMatchGeometry.ExtractNodeGeom(candidate, rootRect);
            if (visibleSize.x <= 0.01f || visibleSize.y <= 0.01f)
            {
                return nodeGeom;
            }

            nodeGeom.sizeLocal = visibleSize;
            nodeGeom.rectMinLocal = nodeGeom.centerLocal - visibleSize * 0.5f;
            nodeGeom.rectMaxLocal = nodeGeom.centerLocal + visibleSize * 0.5f;
            nodeGeom.geometrySource = "popupVisibleSizeTable";
            return nodeGeom;
        }

        private static string GetDominantStdButtonColor(GameObject targetRoot, string folder)
        {
            if (targetRoot == null)
            {
                return null;
            }

            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            RectTransform[] allRects = targetRoot.GetComponentsInChildren<RectTransform>(true);
            for (int i = 0; i < allRects.Length; i++)
            {
                RectTransform rect = allRects[i];
                if (rect == null || !GetStdButtonAssetPath(rect.gameObject, folder, out string assetPath))
                {
                    continue;
                }

                string colorKeyword = ExtractButtonColorKeyword(assetPath);
                if (string.IsNullOrEmpty(colorKeyword))
                {
                    continue;
                }

                counts[colorKeyword] = counts.TryGetValue(colorKeyword, out int count) ? count + 1 : 1;
            }

            if (counts.Count == 0)
            {
                return null;
            }

            return counts
                .OrderByDescending(x => x.Value)
                .ThenBy(x => GetColorPriority(x.Key))
                .Select(x => x.Key)
                .FirstOrDefault();
        }

        private static string ExtractButtonColorKeyword(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return null;
            }

            for (int i = 0; i < ButtonColorPriority.Length; i++)
            {
                if (source.IndexOf(ButtonColorPriority[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ButtonColorPriority[i];
                }
            }

            return null;
        }

        private static int GetColorPriority(string colorKeyword)
        {
            if (string.IsNullOrEmpty(colorKeyword))
            {
                return ButtonColorPriority.Length + 1;
            }

            for (int i = 0; i < ButtonColorPriority.Length; i++)
            {
                if (string.Equals(ButtonColorPriority[i], colorKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return ButtonColorPriority.Length + 1;
        }

        private static bool TryGetPrefabRectSize(string assetPath, out Vector2 size)
        {
            size = Vector2.zero;
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                return false;
            }

            RectTransform rt = prefab.GetComponent<RectTransform>();
            if (rt == null)
            {
                return false;
            }

            size = rt.rect.size;
            if (size.sqrMagnitude <= 0.01f)
            {
                size = rt.sizeDelta;
            }

            return size.sqrMagnitude > 0.01f;
        }

        private static bool IsPrefabInstanceRoot(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return false;
            }

            GameObject prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
            return prefabRoot != null && prefabRoot == gameObject;
        }

        private static bool TryGetPrefabInstanceAssetPath(GameObject gameObject, out string assetPath)
        {
            assetPath = null;
            if (gameObject == null)
            {
                return false;
            }

            GameObject prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
            if (prefabRoot == null)
            {
                return false;
            }

            assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            assetPath = assetPath.Replace("\\", "/");
            return true;
        }

        private static bool IsCommonButtonAssetPath(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath) &&
                   assetPath.Replace("\\", "/").IndexOf("/CommonPrefbs/Btn/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool GetStdButtonAssetPath(GameObject gameObject, string folder, out string assetPath)
        {
            assetPath = null;
            if (gameObject == null || gameObject.GetComponent<UIStdButton>() == null)
            {
                return false;
            }

            GameObject prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
            if (prefabRoot == null || prefabRoot != gameObject)
            {
                return false;
            }

            assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            assetPath = assetPath.Replace("\\", "/");
            return assetPath.StartsWith(NormalizeAssetFolder(folder) + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool GetStdItemAssetPath(GameObject gameObject, string folder, string requiredVariant, out string assetPath)
        {
            assetPath = null;
            if (gameObject == null || gameObject.GetComponent<UIPrefabLink>() == null)
            {
                return false;
            }

            GameObject prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
            if (prefabRoot == null || prefabRoot != gameObject)
            {
                return false;
            }

            assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            assetPath = assetPath.Replace("\\", "/");
            if (!assetPath.StartsWith(NormalizeAssetFolder(folder) + "/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string assetVariant = GetStdItemVariantFromAssetPath(assetPath);
            if (string.IsNullOrEmpty(assetVariant))
            {
                return false;
            }

            string normalizedVariant = NormalizeStdItemVariant(requiredVariant);
            if (!string.IsNullOrEmpty(normalizedVariant) &&
                !string.Equals(assetVariant, normalizedVariant, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static bool GetStdPopupAssetPath(GameObject gameObject, string folder, out string assetPath)
        {
            assetPath = null;
            if (gameObject == null)
            {
                return false;
            }

            GameObject prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
            if (prefabRoot == null || prefabRoot != gameObject)
            {
                return false;
            }

            assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            assetPath = assetPath.Replace("\\", "/");
            if (!assetPath.StartsWith(NormalizeAssetFolder(folder) + "/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string prefabName = Path.GetFileNameWithoutExtension(assetPath);
            return MatchesStdPopupPrefabName(prefabName);
        }

        private static GameObject InstantiateFromScenePoolOrTemplate(string assetPath, Transform desiredParent)
        {
            if (string.IsNullOrEmpty(assetPath) || desiredParent == null)
            {
                return null;
            }

            GameObject pooledInstance = TryInstantiateFromMatchingPool(assetPath, desiredParent);
            if (pooledInstance != null)
            {
                Debug.Log($"[StdPrefab] Instantiate via existing pool parent={GetTransformPath(desiredParent)} asset={assetPath}");
                return pooledInstance;
            }

            if (ShouldPreferPoolInstantiation(desiredParent))
            {
                GameObject generatedPoolInstance = TryInstantiateFromGeneratedPool(assetPath, desiredParent);
                if (generatedPoolInstance != null)
                {
                    Debug.Log($"[StdPrefab] Instantiate via generated pool parent={GetTransformPath(desiredParent)} asset={assetPath}");
                    return generatedPoolInstance;
                }
            }

            GameObject sceneTemplateInstance = TryInstantiateFromMatchingSceneTemplate(assetPath, desiredParent);
            if (sceneTemplateInstance != null)
            {
                Debug.Log($"[StdPrefab] Instantiate via scene template parent={GetTransformPath(desiredParent)} asset={assetPath}");
            }

            return sceneTemplateInstance;
        }

        private static bool ShouldPreferPoolInstantiation(Transform desiredParent)
        {
            if (desiredParent == null)
            {
                return false;
            }

            if (desiredParent.GetComponent<LayoutGroup>() != null)
            {
                return true;
            }

            string parentName = desiredParent.name ?? string.Empty;
            return PSDTagUtility.HasAnyTag(parentName, "@H", "@HLayout", "@V", "@VLayout", "@G", "@Grid");
        }

        private static GameObject TryInstantiateFromMatchingPool(string assetPath, Transform desiredParent)
        {
            UIItemPool[] pools = FindSceneObjectsOfType<UIItemPool>(desiredParent);
            for (int i = 0; i < pools.Length; i++)
            {
                UIItemPool pool = pools[i];
                if (pool == null || pool.itemPrefab == null || pool.panel == null)
                {
                    continue;
                }

                if (pool.panel.transform != desiredParent)
                {
                    continue;
                }

                if (!SceneTemplateMatchesAssetPath(pool.itemPrefab.gameObject, assetPath))
                {
                    continue;
                }

                Transform instance = pool.AddFromPool();
                return instance != null ? instance.gameObject : null;
            }

            return null;
        }

        private static GameObject TryInstantiateFromGeneratedPool(string assetPath, Transform desiredParent)
        {
            UIItemPool pool = GetOrCreateGeneratedPool(assetPath, desiredParent);
            if (pool == null)
            {
                return null;
            }

            Transform instance = pool.AddFromPool();
            return instance != null ? instance.gameObject : null;
        }

        private static UIItemPool GetOrCreateGeneratedPool(string assetPath, Transform desiredParent)
        {
            if (string.IsNullOrEmpty(assetPath) || desiredParent == null)
            {
                return null;
            }

            Transform poolContainerParent = ResolvePoolContainerParent(desiredParent);
            if (poolContainerParent == null)
            {
                return null;
            }

            string key = desiredParent.GetInstanceID() + "|" + assetPath.Replace("\\", "/");
            if (GeneratedItemPoolCache.TryGetValue(key, out UIItemPool cachedPool) && cachedPool != null)
            {
                if (cachedPool.transform.parent != poolContainerParent)
                {
                    cachedPool.transform.SetParent(poolContainerParent, false);
                }

                PositionPoolNextToPanel(cachedPool.transform, desiredParent);
                return cachedPool;
            }

            GameObject poolGo = new GameObject($"{desiredParent.name}_{Path.GetFileNameWithoutExtension(assetPath)}_Pool", typeof(RectTransform), typeof(UIItemPool));
            poolGo.transform.SetParent(poolContainerParent, false);
            PositionPoolNextToPanel(poolGo.transform, desiredParent);
            Undo.RegisterCreatedObjectUndo(poolGo, "Create UIItemPool");

            UIItemPool pool = poolGo.GetComponent<UIItemPool>();
            pool.panel = desiredParent.gameObject;
            pool.ReservedCount = 0;

            GameObject templateGo = CreatePoolTemplateInstance(assetPath, poolGo.transform, desiredParent);
            if (templateGo == null)
            {
                UnityEngine.Object.DestroyImmediate(poolGo);
                return null;
            }

            pool.itemPrefab = templateGo.GetComponent<UIPrefabLink>();

            GeneratedItemPoolCache[key] = pool;
            return pool;
        }

        private static Transform ResolvePoolContainerParent(Transform desiredParent)
        {
            if (desiredParent == null)
            {
                return null;
            }

            if (desiredParent.parent != null)
            {
                return desiredParent.parent;
            }

            Canvas canvas = desiredParent.GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                return canvas.transform;
            }

            return desiredParent.root;
        }

        private static void PositionPoolNextToPanel(Transform poolTransform, Transform panelTransform)
        {
            if (poolTransform == null || panelTransform == null || poolTransform.parent != panelTransform.parent)
            {
                return;
            }

            int panelIndex = panelTransform.GetSiblingIndex();
            int targetIndex = Mathf.Min(panelIndex + 1, poolTransform.parent.childCount - 1);
            poolTransform.SetSiblingIndex(targetIndex);
        }

        private static GameObject CreatePoolTemplateInstance(string assetPath, Transform poolRoot, Transform desiredParent)
        {
            GameObject sceneTemplate = FindMatchingSceneTemplateObject(assetPath, desiredParent);
            GameObject assetPrefab = ResolvePrefabSourceAsset(sceneTemplate, assetPath);
            if (assetPrefab == null)
            {
                return null;
            }

            GameObject templateGo = PrefabUtility.InstantiatePrefab(assetPrefab, poolRoot) as GameObject;

            if (templateGo == null || templateGo.GetComponent<UIPrefabLink>() == null)
            {
                if (templateGo != null)
                {
                    UnityEngine.Object.DestroyImmediate(templateGo);
                }

                return null;
            }

            templateGo.name = Path.GetFileNameWithoutExtension(assetPath);
            templateGo.SetActive(false);
            Undo.RegisterCreatedObjectUndo(templateGo, "Create UIItemPool Template");
            return templateGo;
        }

        private static GameObject TryInstantiateFromMatchingSceneTemplate(string assetPath, Transform desiredParent)
        {
            GameObject sceneTemplate = FindMatchingSceneTemplateObject(assetPath, desiredParent);
            if (sceneTemplate == null)
            {
                return null;
            }

            GameObject assetPrefab = ResolvePrefabSourceAsset(sceneTemplate, assetPath);
            if (assetPrefab == null)
            {
                return null;
            }

            GameObject clone = PrefabUtility.InstantiatePrefab(assetPrefab, desiredParent) as GameObject;
            if (clone != null)
            {
                clone.SetActive(true);
            }

            return clone;
        }

        private static GameObject ResolvePrefabSourceAsset(GameObject sceneTemplate, string assetPath)
        {
            if (sceneTemplate != null)
            {
                string sceneTemplateAssetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(sceneTemplate);
                if (!string.IsNullOrEmpty(sceneTemplateAssetPath))
                {
                    GameObject sceneTemplateAsset = AssetDatabase.LoadAssetAtPath<GameObject>(sceneTemplateAssetPath);
                    if (sceneTemplateAsset != null)
                    {
                        return sceneTemplateAsset;
                    }
                }

                GameObject corresponding = PrefabUtility.GetCorrespondingObjectFromSource(sceneTemplate);
                if (corresponding != null)
                {
                    return corresponding;
                }
            }

            if (string.IsNullOrEmpty(assetPath))
            {
                return null;
            }

            return AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        }

        private static GameObject FindMatchingSceneTemplateObject(string assetPath, Transform desiredParent)
        {
            UIPrefabLink[] templates = FindSceneObjectsOfType<UIPrefabLink>(desiredParent);
            for (int i = 0; i < templates.Length; i++)
            {
                UIPrefabLink link = templates[i];
                if (link == null)
                {
                    continue;
                }

                GameObject templateGo = link.gameObject;
                if (!SceneTemplateMatchesAssetPath(templateGo, assetPath))
                {
                    continue;
                }

                if (!IsSceneTemplateCloneCandidate(templateGo))
                {
                    continue;
                }

                return templateGo;
            }

            return null;
        }

        private static bool IsSceneTemplateCloneCandidate(GameObject templateGo)
        {
            if (templateGo == null)
            {
                return false;
            }

            GameObject prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(templateGo);
            if (prefabRoot == null || prefabRoot != templateGo)
            {
                return false;
            }

            if (HasAncestorComponent<UIItemPool>(templateGo.transform))
            {
                return true;
            }

            return !templateGo.activeInHierarchy;
        }

        private static bool SceneTemplateMatchesAssetPath(GameObject sceneObject, string assetPath)
        {
            if (sceneObject == null || string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            string sceneAssetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(sceneObject);
            if (string.IsNullOrEmpty(sceneAssetPath))
            {
                return false;
            }

            return string.Equals(
                sceneAssetPath.Replace("\\", "/"),
                assetPath.Replace("\\", "/"),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasAncestorComponent<T>(Transform transform) where T : Component
        {
            Transform cursor = transform;
            while (cursor != null)
            {
                if (cursor.GetComponent<T>() != null)
                {
                    return true;
                }

                cursor = cursor.parent;
            }

            return false;
        }

        private static T[] FindSceneObjectsOfType<T>(Transform context) where T : Component
        {
            if (context == null)
            {
                return new T[0];
            }

            Scene scene = context.gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return new T[0];
            }

            List<T> results = new List<T>();
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                if (root == null)
                {
                    continue;
                }

                results.AddRange(root.GetComponentsInChildren<T>(true));
            }

            return results.ToArray();
        }

        private static string GetStdItemVariantFromAssetPath(string assetPath)
        {
            string prefabName = Path.GetFileNameWithoutExtension(assetPath ?? string.Empty);
            if (string.Equals(prefabName, StdItemBoxPrefabName, StringComparison.OrdinalIgnoreCase))
            {
                return StdItemVariantBox;
            }

            if (string.Equals(prefabName, StdItemCirclePrefabName, StringComparison.OrdinalIgnoreCase))
            {
                return StdItemVariantCircle;
            }

            return string.Empty;
        }

        private static string GetStdItemPrefabNameByVariant(string variant)
        {
            string normalized = NormalizeStdItemVariant(variant);
            if (string.Equals(normalized, StdItemVariantBox, StringComparison.OrdinalIgnoreCase))
            {
                return StdItemBoxPrefabName;
            }

            if (string.Equals(normalized, StdItemVariantCircle, StringComparison.OrdinalIgnoreCase))
            {
                return StdItemCirclePrefabName;
            }

            return string.Empty;
        }

        private static bool MatchesStdItemPrefabName(string prefabName)
        {
            return string.Equals(prefabName, StdItemBoxPrefabName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(prefabName, StdItemCirclePrefabName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesStdPopupPrefabName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
            {
                return false;
            }

            for (int i = 0; i < PopupPrefabInfos.Length; i++)
            {
                if (string.Equals(PopupPrefabInfos[i].prefabName, prefabName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetStdPopupVisibleSize(string assetPath, out Vector2 visibleSize)
        {
            visibleSize = Vector2.zero;
            string prefabName = Path.GetFileNameWithoutExtension(assetPath ?? string.Empty);
            for (int i = 0; i < PopupPrefabInfos.Length; i++)
            {
                if (string.Equals(PopupPrefabInfos[i].prefabName, prefabName, StringComparison.OrdinalIgnoreCase))
                {
                    visibleSize = PopupPrefabInfos[i].visibleSize;
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeStdItemVariant(string variant)
        {
            if (string.IsNullOrWhiteSpace(variant))
            {
                return string.Empty;
            }

            if (string.Equals(variant, StdItemVariantBox, StringComparison.OrdinalIgnoreCase))
            {
                return StdItemVariantBox;
            }

            if (string.Equals(variant, StdItemVariantCircle, StringComparison.OrdinalIgnoreCase))
            {
                return StdItemVariantCircle;
            }

            return string.Empty;
        }

        private static string NormalizeAssetFolder(string folder)
        {
            return (folder ?? string.Empty).Replace("\\", "/").TrimEnd('/');
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
