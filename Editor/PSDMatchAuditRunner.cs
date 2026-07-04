using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityObject = UnityEngine.Object;

namespace PSDImporter
{
    public static class PSDMatchAuditRunner
    {
        private const string DefaultConfigPath = "Assets/_OpenCode/TZUI/PS/PSDTools/Editor/PSDMatchAuditConfig.asset";
        private const string DefaultOutputFolder = "Assets/_OpenCode/TZUI/PS/PSDTools/Log/Audit";
        private const float PoorOverlapThreshold = 0.5f;
        private const float LargeCenterDiagonalRatio = 0.35f;
        private const float LargeSizeErrorRatio = 0.25f;
        private const int SuspectCropPadding = 80;

        private static readonly Color32 PsdColor = new Color32(45, 160, 255, 255);
        private static readonly Color32 MatchedColor = new Color32(255, 176, 45, 255);
        private static readonly Color32 SuspectColor = new Color32(255, 55, 55, 255);
        private static readonly Color32[] CandidateColors =
        {
            new Color32(70, 255, 95, 255),
            new Color32(255, 75, 220, 255),
            new Color32(255, 235, 45, 255),
            new Color32(60, 235, 255, 255),
            new Color32(245, 245, 245, 255)
        };

        [Serializable]
        private class AuditSummary
        {
            public string timestamp;
            public string psdDataPath;
            public string targetRootPath;
            public string outputFolder;
            public string fullPsdImage;
            public string templatePsdImage;
            public string layerCompositeImage;
            public string fullOverlayImage;
            public string unityRenderImage;
            public string unityRenderSource;
            public int unityRenderWidth;
            public int unityRenderHeight;
            public string unityRenderNote;
            public bool templateFound;
            public string templateSourcePath;
            public string primaryPsdSource;
            public ImageCompareLog templateVsLayerComposite;
            public int psdWidth;
            public int psdHeight;
            public int totalLayers;
            public int matchedLayers;
            public int unmatchedLayers;
            public int suspectLayers;
            public int maxTopCandidates;
            public int captureScale;
            public string runMode;
            public AuditConfigLog config;
        }

        [Serializable]
        private class ImageCompareLog
        {
            public bool available;
            public string imageA;
            public string imageB;
            public int width;
            public int height;
            public int comparedPixels;
            public float meanAbsoluteRgbDiff;
            public float differentPixelRatio;
            public float differentPixelThreshold;
            public string note;
        }

        [Serializable]
        private class AuditConfigLog
        {
            public float maxDistanceError;
            public float maxSizeDiff;
            public float weightPosition;
            public float weightSize;
            public float weightType;
            public float weightDepth;
            public float weightAnchor;
            public int maxDepthDiff;
            public bool enableIdHistoryMatch;
            public float lowConfidenceMargin;
            public bool enableGeometryReject;
            public float geometryRejectDistanceMultiplier;
            public float geometryRejectSizeRatio;
            public bool enableCandidatePruning;
            public float candidateDistanceMultiplier;
            public bool preLockEnabled;
            public bool preLockUseDynamicThreshold;
            public float preLockDynamicRatio;
            public float preLockDynamicMinThreshold;
            public bool autoSlice;
            public bool dedupeSprites;
            public float textLayoutPaddingX;
            public float textLayoutPaddingY;
            public bool textPreserveLargerLayoutRect;
            public float textSingleLineExpansionMaxRatio;
            public float textSingleLineExpansionMaxExtraWidth;
            public float textSizeScoreWeightMultiplier;
            public float textGeometryRejectSizeRatio;
        }

        [Serializable]
        private class AuditSuspectsFile
        {
            public AuditSummary summary;
            public List<AuditLayer> suspects = new List<AuditLayer>();
        }

        [Serializable]
        private class AuditAllLayersFile
        {
            public AuditSummary summary;
            public List<AuditLayer> layers = new List<AuditLayer>();
        }

        [Serializable]
        private class AgentReviewTemplate
        {
            public string generatedAt;
            public string sourceSuspectsJson;
            public List<AgentReviewItem> reviews = new List<AgentReviewItem>();
        }

        [Serializable]
        private class AgentReviewItem
        {
            public int layerId;
            public string pngName;
            public string verdict;
            public string correctNodePath;
            public string causeTag;
            public string notes;
        }

        [Serializable]
        private class AuditLayer
        {
            public int layerId;
            public string pngName;
            public string uiType;
            public string groupName;
            public string matchedNodePath;
            public string matchMethod;
            public string matchedGeometrySource;
            public float score;
            public float bestCandidateScore;
            public float secondBestCandidateScore;
            public float scoreMargin;
            public bool isLowConfidence;
            public bool isConfirmed;
            public string statusInfo;
            public RectLog psdRect;
            public RectLog matchedRect;
            public RectLog matchedLayoutRect;
            public float iou;
            public float centerDistance;
            public float maxSizeErrorRatio;
            public bool idHistoryRejected;
            public string idHistoryRejectedPath;
            public string idHistoryRejectReason;
            public PSDImageReuseLog imageReuse;
            public int selectedCandidateRank;
            public int skippedTypeCandidates;
            public int skippedSpatialCandidates;
            public int hierarchyPenalizedCandidates;
            public List<string> reasons = new List<string>();
            public List<string> causeTags = new List<string>();
            public List<AuditCandidate> topCandidates = new List<AuditCandidate>();
            public string suspectImage;
        }

        [Serializable]
        private class AuditCandidate
        {
            [JsonIgnore] public RectTransform node;
            public string nodePath;
            public string color;
            public string geometrySource;
            public float score;
            public float typeScore;
            public float distance;
            public float diffW;
            public float diffH;
            public float scorePos;
            public float scoreSize;
            public float scoreType;
            public float scoreDepth;
            public float scoreAnchor;
            public RectLog rect;
        }

        [Serializable]
        private class RectLog
        {
            public float centerX;
            public float centerY;
            public float width;
            public float height;
            public float rectMinX;
            public float rectMinY;
            public float rectMaxX;
            public float rectMaxY;
            public int pixelX;
            public int pixelY;
            public int pixelW;
            public int pixelH;
        }

        [MenuItem("PSD2NGUI/Agent/Create Default Audit Config", priority = 300)]
        public static void CreateDefaultAuditConfigMenu()
        {
            PSDMatchAuditConfig config = GetOrCreateDefaultConfig();
            Selection.activeObject = config;
            EditorGUIUtility.PingObject(config);
        }

        [MenuItem("PSD2NGUI/Agent/Run Restore Audit", priority = 301)]
        public static void RunRestoreAuditMenu()
        {
            PSDMatchAuditConfig config = GetActiveOrDefaultConfig();
            string folder = Run(config);
            if (!string.IsNullOrEmpty(folder))
            {
                Debug.Log($"[MatchAudit] Audit exported: {folder}");
            }
        }

        [MenuItem("PSD2NGUI/Debug/Run Match Audit Smoke", priority = 203)]
        public static void RunMatchAuditSmoke()
        {
            PSDMatchAuditConfig config = ScriptableObject.CreateInstance<PSDMatchAuditConfig>();
            config.psdDataPath = FindFirstPsDataPath();
            config.targetRoot = FindFirstSceneRectRoot();
            config.importConfig = FindFirstAsset<PSDImportConfig>();
            config.maxTopCandidates = 3;
            config.maxSuspects = 10;
            config.captureScale = 1;
            config.outputFolder = DefaultOutputFolder;
            config.runMode = PSDMatchAuditRunMode.DryRunAudit;

            string folder = Run(config);
            UnityObject.DestroyImmediate(config);
            if (string.IsNullOrEmpty(folder))
            {
                Debug.LogError("[MatchAuditSmoke] FAIL");
                return;
            }

            string abs = ToAbsolutePath(folder);
            bool ok = File.Exists(Path.Combine(abs, "audit_summary.json")) &&
                      File.Exists(Path.Combine(abs, "suspects.json")) &&
                      File.Exists(Path.Combine(abs, "all_layers.json")) &&
                      File.Exists(Path.Combine(abs, "layer_composite.png")) &&
                      File.Exists(Path.Combine(abs, "full_overlay.png"));
            Debug.Log(ok ? "[MatchAuditSmoke] PASS" : "[MatchAuditSmoke] FAIL");
        }

        public static string Run(PSDMatchAuditConfig auditConfig)
        {
            if (auditConfig == null)
            {
                Debug.LogError("[MatchAudit] Missing PSDMatchAuditConfig.");
                return null;
            }

            PSDImportConfig importConfig = auditConfig.importConfig != null ? auditConfig.importConfig : FindFirstAsset<PSDImportConfig>();
            bool ownsImportConfig = false;
            if (importConfig == null)
            {
                importConfig = ScriptableObject.CreateInstance<PSDImportConfig>();
                ownsImportConfig = true;
            }

            string psdPath = ResolvePsdDataPath(auditConfig);
            if (string.IsNullOrEmpty(psdPath))
            {
                Debug.LogError("[MatchAudit] No .ps.data input found.");
                CleanupTemp(importConfig, ownsImportConfig);
                return null;
            }

            PSDData psdData = PSDLoader.ReadJson(psdPath);
            if (psdData == null)
            {
                Debug.LogError($"[MatchAudit] Failed to read PSD data: {psdPath}");
                CleanupTemp(importConfig, ownsImportConfig);
                return null;
            }

            GameObject targetRoot = ResolveTargetRoot(auditConfig);
            if (targetRoot == null)
            {
                Debug.LogError("[MatchAudit] No target root found.");
                CleanupTemp(importConfig, ownsImportConfig);
                return null;
            }

            GameObject activeRoot = null;
            GameObject tempAuditRoot = null;
            string prefabAssetPath = null;
            bool isPrefabAsset = IsPrefabAsset(targetRoot, out prefabAssetPath);
            Vector2Int targetRenderSize = ResolveAuditRenderSize(targetRoot.transform as RectTransform, psdData);
            if (auditConfig.auditRenderWidth > 0 && auditConfig.auditRenderHeight > 0)
            {
                targetRenderSize = new Vector2Int(auditConfig.auditRenderWidth, auditConfig.auditRenderHeight);
            }
            try
            {
                if (isPrefabAsset)
                {
                    activeRoot = PrefabUtility.LoadPrefabContents(prefabAssetPath);
                }
                else if (auditConfig.runMode == PSDMatchAuditRunMode.DryRunAudit)
                {
                    tempAuditRoot = UnityObject.Instantiate(targetRoot);
                    tempAuditRoot.name = targetRoot.name + "_PSDToolsAuditApplyPreview";
                    tempAuditRoot.hideFlags = HideFlags.HideAndDontSave;
                    tempAuditRoot.SetActive(true);
                    activeRoot = tempAuditRoot;
                }
                else
                {
                    activeRoot = targetRoot;
                }

                if (activeRoot == null)
                {
                    Debug.LogError($"[MatchAudit] Failed to load target root: {GetTargetDisplayPath(targetRoot)}");
                    return null;
                }

                PSDMatchGeometry.RebuildLayoutForGeometry(activeRoot.transform as RectTransform);
                PSDBindingData bindingAsset = LoadBindingAsset(psdPath, auditConfig.runMode == PSDMatchAuditRunMode.ApplyAndSave);
                List<BindingPairViewModel> bindings = BuildBindings(psdData);
                VisualBindingRestoreService.RunAutoMatch(bindings, activeRoot, psdData, bindingAsset, importConfig);
                bool simulateApplyForAudit = auditConfig.runMode == PSDMatchAuditRunMode.ApplyAndSave || isPrefabAsset || tempAuditRoot != null;
                if (simulateApplyForAudit)
                {
                    PSDBindingData applyBindingAsset = auditConfig.runMode == PSDMatchAuditRunMode.ApplyAndSave ? bindingAsset : null;
                    VisualBindingRestoreService.ApplyBindings(
                        bindings,
                        activeRoot,
                        psdData,
                        applyBindingAsset,
                        importConfig,
                        psdPath,
                        auditConfig.runMode == PSDMatchAuditRunMode.ApplyAndSave && !isPrefabAsset,
                        auditConfig.runMode == PSDMatchAuditRunMode.ApplyAndSave);
                    PSDMatchGeometry.RebuildLayoutForGeometry(activeRoot.transform as RectTransform);
                }

                string output = ExportAudit(
                    psdPath,
                    activeRoot,
                    psdData,
                    bindings,
                    importConfig,
                    auditConfig.outputFolder,
                    auditConfig.maxTopCandidates,
                    auditConfig.maxSuspects,
                    auditConfig.captureScale,
                    targetRenderSize,
                    simulateApplyForAudit
                        ? $"{auditConfig.runMode}+SimulatedApply"
                        : auditConfig.runMode.ToString());

                if (auditConfig.runMode == PSDMatchAuditRunMode.ApplyAndSave)
                {
                    if (isPrefabAsset)
                    {
                        PrefabUtility.SaveAsPrefabAsset(activeRoot, prefabAssetPath);
                    }

                    AssetDatabase.SaveAssets();
                }

                return output;
            }
            finally
            {
                if (isPrefabAsset && activeRoot != null)
                {
                    PrefabUtility.UnloadPrefabContents(activeRoot);
                }

                if (tempAuditRoot != null)
                {
                    UnityObject.DestroyImmediate(tempAuditRoot);
                }

                CleanupTemp(importConfig, ownsImportConfig);
            }
        }

        public static string ExportAuditFromCurrentState(
            UnityObject psdDataFile,
            GameObject activeRoot,
            PSDImportConfig importConfig,
            PSDData psdData,
            List<BindingPairViewModel> bindings)
        {
            if (psdDataFile == null || activeRoot == null || psdData == null || bindings == null)
            {
                Debug.LogError("[MatchAudit] Current window state is incomplete.");
                return null;
            }

            string psdPath = AssetDatabase.GetAssetPath(psdDataFile);
            if (string.IsNullOrEmpty(psdPath))
            {
                Debug.LogError("[MatchAudit] Cannot resolve PSD data asset path.");
                return null;
            }

            PSDImportConfig activeConfig = importConfig != null ? importConfig : FindFirstAsset<PSDImportConfig>();
            bool ownsImportConfig = false;
            if (activeConfig == null)
            {
                activeConfig = ScriptableObject.CreateInstance<PSDImportConfig>();
                ownsImportConfig = true;
            }

            try
            {
                string output = ExportAudit(
                    psdPath,
                    activeRoot,
                    psdData,
                    bindings,
                    activeConfig,
                    DefaultOutputFolder,
                    5,
                    12,
                    1,
                    ResolveAuditRenderSize(activeRoot.transform as RectTransform, psdData),
                    "WindowCurrentState");
                Debug.Log($"[MatchAudit] Audit exported: {output}");
                return output;
            }
            finally
            {
                CleanupTemp(activeConfig, ownsImportConfig);
            }
        }

        private static string ExportAudit(
            string psdPath,
            GameObject activeRoot,
            PSDData psdData,
            List<BindingPairViewModel> bindings,
            PSDImportConfig importConfig,
            string outputFolder,
            int maxTopCandidates,
            int maxSuspects,
            int captureScale,
            Vector2Int auditRenderSize,
            string runMode)
        {
            RectTransform rootRect = activeRoot != null ? activeRoot.transform as RectTransform : null;
            if (rootRect == null)
            {
                Debug.LogError("[MatchAudit] Target root must be a RectTransform.");
                return null;
            }

            maxTopCandidates = Mathf.Max(1, maxTopCandidates);
            maxSuspects = Mathf.Max(1, maxSuspects);
            captureScale = Mathf.Clamp(captureScale, 1, 4);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string psdName = SanitizeFileName(Path.GetFileNameWithoutExtension(psdPath));
            string outputAssetFolder = NormalizeAssetPath(string.IsNullOrEmpty(outputFolder) ? DefaultOutputFolder : outputFolder);
            string runFolder = $"{outputAssetFolder}/Audit_{psdName}_{timestamp}";
            string absRunFolder = ToAbsolutePath(runFolder);
            string absSuspectFolder = Path.Combine(absRunFolder, "suspects");
            Directory.CreateDirectory(absRunFolder);
            Directory.CreateDirectory(absSuspectFolder);

            Texture2D layerComposite = BuildPsdComposite(psdData, bindings, captureScale);
            string templateSourcePath;
            Texture2D templatePsd = LoadTemplateTexture(psdData, captureScale, out templateSourcePath);
            Texture2D fullPsd = CloneTexture(templatePsd != null ? templatePsd : layerComposite);
            Texture2D fullOverlay = CloneTexture(fullPsd);
            ImageCompareLog templateCompare = BuildImageCompareLog(
                templatePsd,
                layerComposite,
                $"{runFolder}/template_psd.png",
                $"{runFolder}/layer_composite.png");

            var allLayers = new List<AuditLayer>();
            var suspects = new List<AuditLayer>();
            Dictionary<int, BindingPairViewModel> idToBind = bindings
                .Where(b => b != null && b.psdItem.id != 0)
                .GroupBy(b => b.psdItem.id)
                .ToDictionary(g => g.Key, g => g.First());

            for (int i = 0; i < bindings.Count; i++)
            {
                BindingPairViewModel bind = bindings[i];
                AuditLayer layer = BuildLayerAudit(bind, rootRect, psdData, importConfig, maxTopCandidates, idToBind, captureScale);
                allLayers.Add(layer);

                DrawRectOutline(fullOverlay, ToPixelRect(layer.psdRect), PsdColor, 1);
                if (layer.matchedRect != null)
                {
                    DrawRectOutline(fullOverlay, ToPixelRect(layer.matchedRect), MatchedColor, 1);
                }

                if (layer.reasons.Count > 0 && suspects.Count < maxSuspects)
                {
                    suspects.Add(layer);
                }
            }

            for (int i = 0; i < suspects.Count; i++)
            {
                AuditLayer suspect = suspects[i];
                DrawRectOutline(fullOverlay, ToPixelRect(suspect.psdRect), SuspectColor, 3);
                if (suspect.matchedRect != null)
                {
                    DrawRectOutline(fullOverlay, ToPixelRect(suspect.matchedRect), SuspectColor, 3);
                }
            }

            fullPsd.Apply();
            fullOverlay.Apply();
            WriteTexture(layerComposite, Path.Combine(absRunFolder, "layer_composite.png"));
            if (templatePsd != null)
            {
                WriteTexture(templatePsd, Path.Combine(absRunFolder, "template_psd.png"));
            }
            WriteTexture(fullOverlay, Path.Combine(absRunFolder, "full_overlay.png"));

            string unityRenderImage = string.Empty;
            string unityRenderNote = string.Empty;
            Vector2Int unityRenderSize = auditRenderSize.x > 0 && auditRenderSize.y > 0
                ? auditRenderSize
                : ResolveAuditRenderSize(rootRect, psdData);
            try
            {
                unityRenderImage = PSDRenderCaptureUtility.CaptureToPng(
                    activeRoot,
                    unityRenderSize.x,
                    unityRenderSize.y,
                    $"{runFolder}/unity_render.png");
            }
            catch (Exception ex)
            {
                unityRenderNote = ex.Message;
                Debug.LogWarning($"[MatchAudit] Failed to capture restored render: {ex.Message}");
            }

            for (int i = 0; i < suspects.Count; i++)
            {
                Texture2D suspectImage = BuildSuspectImage(fullPsd, suspects[i], captureScale);
                string fileName = $"{i + 1:000}_{SanitizeFileName(suspects[i].pngName)}.png";
                string absPath = Path.Combine(absSuspectFolder, fileName);
                WriteTexture(suspectImage, absPath);
                UnityObject.DestroyImmediate(suspectImage);
                suspects[i].suspectImage = ToAssetPath(absPath);
            }

            AuditSummary summary = new AuditSummary
            {
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                psdDataPath = NormalizeAssetPath(psdPath),
                targetRootPath = GetTransformPath(activeRoot.transform, null),
                outputFolder = runFolder,
                fullPsdImage = templatePsd != null ? $"{runFolder}/template_psd.png" : $"{runFolder}/layer_composite.png",
                templatePsdImage = templatePsd != null ? $"{runFolder}/template_psd.png" : string.Empty,
                layerCompositeImage = $"{runFolder}/layer_composite.png",
                fullOverlayImage = $"{runFolder}/full_overlay.png",
                unityRenderImage = unityRenderImage,
                unityRenderSource = runMode.Contains("SimulatedApply") || runMode.Contains("ApplyAndSave")
                    ? "restore_applied_audit_root"
                    : "target_root_current_state",
                unityRenderWidth = unityRenderSize.x,
                unityRenderHeight = unityRenderSize.y,
                unityRenderNote = unityRenderNote,
                templateFound = templatePsd != null,
                templateSourcePath = templateSourcePath ?? string.Empty,
                primaryPsdSource = templatePsd != null ? "template" : "layerComposite",
                templateVsLayerComposite = templateCompare,
                psdWidth = psdData.width,
                psdHeight = psdData.height,
                totalLayers = bindings.Count,
                matchedLayers = bindings.Count(b => b.unityNode != null),
                unmatchedLayers = bindings.Count(b => b.unityNode == null),
                suspectLayers = suspects.Count,
                maxTopCandidates = maxTopCandidates,
                captureScale = captureScale,
                runMode = runMode,
                config = BuildConfigLog(importConfig)
            };

            WriteJson(Path.Combine(absRunFolder, "audit_summary.json"), summary);
            WriteJson(Path.Combine(absRunFolder, "suspects.json"), new AuditSuspectsFile
            {
                summary = summary,
                suspects = suspects
            });
            WriteJson(Path.Combine(absRunFolder, "all_layers.json"), new AuditAllLayersFile
            {
                summary = summary,
                layers = allLayers
            });
            WriteJson(Path.Combine(absRunFolder, "agent_review_template.json"), BuildReviewTemplate(suspects, $"{runFolder}/suspects.json"));

            UnityObject.DestroyImmediate(layerComposite);
            if (templatePsd != null)
            {
                UnityObject.DestroyImmediate(templatePsd);
            }
            UnityObject.DestroyImmediate(fullPsd);
            UnityObject.DestroyImmediate(fullOverlay);
            AssetDatabase.Refresh();
            Debug.Log($"[MatchAudit] Exported {suspects.Count}/{bindings.Count} suspects to {runFolder}");
            return runFolder;
        }

        private static Vector2Int ResolveAuditRenderSize(RectTransform rootRect, PSDData psdData)
        {
            if (rootRect != null && rootRect.rect.width > 0f && rootRect.rect.height > 0f)
            {
                return new Vector2Int(
                    Mathf.Max(1, Mathf.RoundToInt(rootRect.rect.width)),
                    Mathf.Max(1, Mathf.RoundToInt(rootRect.rect.height)));
            }

            return new Vector2Int(Mathf.Max(1, psdData.width), Mathf.Max(1, psdData.height));
        }

        private static AuditLayer BuildLayerAudit(
            BindingPairViewModel bind,
            RectTransform rootRect,
            PSDData psdData,
            PSDImportConfig config,
            int maxTopCandidates,
            Dictionary<int, BindingPairViewModel> idToBind,
            int scale)
        {
            PSDMatchGeometry.PsdGeom psdGeom = PSDMatchGeometry.BuildPsdGeom(bind.psdItem, psdData.width, psdData.height);
            RectLog psdRect = ToRectLog(psdGeom, psdData.width, psdData.height, scale);
            RectTransform matchedRectTransform = bind.unityNode != null ? bind.unityNode as RectTransform : null;
            PSDMatchGeometry.NodeGeom matchedGeom = matchedRectTransform != null
                ? PSDMatchGeometry.ExtractNodeGeom(matchedRectTransform, rootRect)
                : default;

            List<AuditCandidate> top = BuildTopCandidates(bind.psdItem, psdGeom, rootRect, psdData, config, maxTopCandidates, scale);
            int selectedRank = -1;
            if (matchedRectTransform != null)
            {
                for (int i = 0; i < top.Count; i++)
                {
                    if (top[i].node == matchedRectTransform)
                    {
                        selectedRank = i + 1;
                        break;
                    }
                }
            }

            AuditLayer layer = new AuditLayer
            {
                layerId = bind.psdItem.id,
                pngName = bind.psdItem.pngName,
                uiType = bind.psdItem.uiType,
                groupName = bind.psdItem.groupName,
                matchedNodePath = bind.unityNode != null ? GetTransformPath(bind.unityNode, rootRect) : null,
                matchMethod = GetMatchMethod(bind),
                matchedGeometrySource = matchedRectTransform != null ? matchedGeom.geometrySource : null,
                score = bind.score,
                bestCandidateScore = bind.bestCandidateScore,
                secondBestCandidateScore = bind.secondBestCandidateScore,
                scoreMargin = bind.scoreMargin,
                isLowConfidence = bind.isLowConfidence,
                isConfirmed = bind.isConfirmed,
                statusInfo = bind.statusInfo,
                psdRect = psdRect,
                matchedRect = matchedRectTransform != null ? ToRectLog(matchedGeom, psdData.width, psdData.height, scale) : null,
                matchedLayoutRect = matchedRectTransform != null && matchedGeom.hasLayoutBounds
                    ? ToRectLog(
                        matchedGeom.layoutCenterLocal,
                        matchedGeom.layoutSizeLocal,
                        matchedGeom.layoutRectMinLocal,
                        matchedGeom.layoutRectMaxLocal,
                        psdData.width,
                        psdData.height,
                        scale)
                    : null,
                idHistoryRejected = bind.idHistoryRejected,
                idHistoryRejectedPath = bind.idHistoryRejectedPath,
                idHistoryRejectReason = bind.idHistoryRejectReason,
                imageReuse = PSDImageReuseLogStore.GetOrBuildCurrent(bind.psdItem, bind.unityNode, psdData.psdAssetsFolder, config),
                selectedCandidateRank = selectedRank,
                skippedTypeCandidates = bind.skippedTypeCandidates,
                skippedSpatialCandidates = bind.skippedSpatialCandidates,
                hierarchyPenalizedCandidates = bind.hierarchyPenalizedCandidates,
                topCandidates = top
            };

            if (matchedRectTransform != null)
            {
                layer.iou = CalculateIou(psdGeom.rectMinLocal, psdGeom.rectMaxLocal, matchedGeom.rectMinLocal, matchedGeom.rectMaxLocal);
                layer.centerDistance = Vector2.Distance(psdGeom.centerLocal, matchedGeom.centerLocal);
                layer.maxSizeErrorRatio = CalculateMaxSizeErrorRatio(psdGeom.sizeLocal, matchedGeom.sizeLocal);
            }

            AddSuspectReasons(layer, bind, psdGeom, matchedRectTransform, matchedGeom, idToBind, config);
            return layer;
        }

        private static void AddSuspectReasons(
            AuditLayer layer,
            BindingPairViewModel bind,
            PSDMatchGeometry.PsdGeom psdGeom,
            RectTransform matchedRectTransform,
            PSDMatchGeometry.NodeGeom matchedGeom,
            Dictionary<int, BindingPairViewModel> idToBind,
            PSDImportConfig config)
        {
            bool isTextLayer = bind.psdItem.isText ||
                               string.Equals(bind.psdItem.uiType, "Text", StringComparison.OrdinalIgnoreCase) ||
                               (matchedRectTransform != null && matchedRectTransform.GetComponent<Text>() != null);

            if (bind.isAutoCreated)
            {
                AddReason(layer, "AutoCreated", "new_node");
            }

            if (bind.idHistoryRejected)
            {
                AddReason(layer, $"IDHistoryRejected path={bind.idHistoryRejectedPath} reason={bind.idHistoryRejectReason}", "id_history");
            }

            if (layer.imageReuse != null)
            {
                if (string.Equals(layer.imageReuse.sourceKind, PSDImageReuseSourceKind.Missing.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    AddReason(layer, $"ImageReuseMissing reason={layer.imageReuse.rejectReason}", "image_reuse");
                }
                else if (!string.IsNullOrEmpty(layer.imageReuse.rejectReason))
                {
                    AddReason(layer, $"ImageReuseRejected reason={layer.imageReuse.rejectReason}", "image_reuse");
                }

                if (bind.psdItem.hasSlice && !layer.imageReuse.hasSlice)
                {
                    AddReason(layer, "NineSliceExpectedButNotApplied", "nine_slice");
                }
            }

            if (bind.unityNode == null)
            {
                AddReason(layer, "Unmatched", "missing_node");
                return;
            }

            if (bind.isLowConfidence)
            {
                AddReason(layer, "LowConfidence", "duplicate_list");
            }

            if (matchedRectTransform != null)
            {
                float poorOverlapThreshold = isTextLayer ? 0.25f : PoorOverlapThreshold;
                float largeSizeErrorThreshold = isTextLayer && config != null
                    ? Mathf.Max(LargeSizeErrorRatio, config.textGeometryRejectSizeRatio)
                    : LargeSizeErrorRatio;

                if (layer.iou < poorOverlapThreshold)
                {
                    AddReason(layer, $"PoorGeometryOverlap iou={layer.iou:F2}", "geometry_score");
                }

                float diag = Mathf.Max(20f, psdGeom.sizeLocal.magnitude);
                if (layer.centerDistance > diag * LargeCenterDiagonalRatio)
                {
                    AddReason(layer, $"LargeCenterError distance={layer.centerDistance:F1}", "geometry_score");
                }

                if (layer.maxSizeErrorRatio > largeSizeErrorThreshold)
                {
                    AddReason(layer, $"LargeSizeError ratio={layer.maxSizeErrorRatio:F2}", "geometry_score");
                }

                if (layer.selectedCandidateRank > 1 && layer.topCandidates.Count > 0)
                {
                    float gap = layer.topCandidates[0].score - bind.score;
                    float minGap = config != null ? Mathf.Max(10f, config.lowConfidenceMargin) : 10f;
                    if (gap > minGap)
                    {
                        AddReason(layer, $"HungarianDisplaced top1Gap={gap:F1}", "geometry_score");
                    }
                }

                if (bind.psdItem.parentNodeId > 0 &&
                    idToBind.TryGetValue(bind.psdItem.parentNodeId, out BindingPairViewModel parentBind) &&
                    parentBind.unityNode != null &&
                    !matchedRectTransform.IsChildOf(parentBind.unityNode))
                {
                    AddReason(layer, "HierarchyOutsideMatchedParent", "hierarchy");
                }
            }

            if (bind.skippedSpatialCandidates > 0 &&
                (layer.reasons.Count > 0 || bind.isLowConfidence || layer.selectedCandidateRank > 1))
            {
                AddReason(layer, $"CandidatePruned spatial={bind.skippedSpatialCandidates}", "candidate_pruned");
            }

            if (bind.hierarchyPenalizedCandidates > 0)
            {
                AddReason(layer, $"HierarchyPenalty candidates={bind.hierarchyPenalizedCandidates}", "hierarchy");
            }

            if (layer.topCandidates.Count > 1 && layer.topCandidates[0].score - layer.topCandidates[1].score < (config != null ? config.lowConfidenceMargin : 15f))
            {
                AddReason(layer, "DuplicateAmbiguity", "duplicate_list");
            }
        }

        private static List<AuditCandidate> BuildTopCandidates(
            PicData item,
            PSDMatchGeometry.PsdGeom psdGeom,
            RectTransform rootRect,
            PSDData psdData,
            PSDImportConfig config,
            int maxTopCandidates,
            int scale)
        {
            var candidates = new List<AuditCandidate>();
            RectTransform[] nodes = rootRect.GetComponentsInChildren<RectTransform>(true);
            for (int i = 0; i < nodes.Length; i++)
            {
                RectTransform node = nodes[i];
                if (node == null || node == rootRect)
                {
                    continue;
                }

                if (PSDMatchNodeFilter.ShouldSkipInactiveMatch(node, rootRect, config))
                {
                    continue;
                }

                float typeScore = PSDMatchTypeUtility.GetTypeMatchScore(node, item, config != null ? config.typeCompatScore : 0f);
                if (typeScore <= 0f)
                {
                    continue;
                }

                PSDMatchGeometry.NodeGeom nodeGeom = PSDMatchGeometry.ExtractNodeGeom(node, rootRect);
                PSDMatchScoring.ScoreBreakdown breakdown = VisualBindingRestoreService.CalculateScoreFromGeometry(nodeGeom, psdGeom, typeScore, config, 1f, item, node);
                if (breakdown.total <= 1f)
                {
                    continue;
                }

                if (IsPrunedByDistance(breakdown.geometry.distance, config))
                {
                    continue;
                }

                candidates.Add(new AuditCandidate
                {
                    node = node,
                    nodePath = GetTransformPath(node, rootRect),
                    geometrySource = nodeGeom.geometrySource,
                    score = breakdown.total,
                    typeScore = typeScore,
                    distance = breakdown.geometry.distance,
                    diffW = breakdown.geometry.diffW,
                    diffH = breakdown.geometry.diffH,
                    scorePos = breakdown.scorePos,
                    scoreSize = breakdown.scoreSize,
                    scoreType = breakdown.scoreType,
                    scoreDepth = breakdown.scoreDepth,
                    scoreAnchor = breakdown.scoreAnchor,
                    rect = ToRectLog(nodeGeom, psdData.width, psdData.height, scale)
                });
            }

            candidates.Sort((a, b) => b.score.CompareTo(a.score));
            int count = Mathf.Min(maxTopCandidates, candidates.Count);
            var top = candidates.Take(count).ToList();
            for (int i = 0; i < top.Count; i++)
            {
                top[i].color = ToHex(CandidateColors[Mathf.Min(i, CandidateColors.Length - 1)]);
            }

            return top;
        }

        private static bool IsPrunedByDistance(float distance, PSDImportConfig config)
        {
            if (config == null || !config.enableCandidatePruning || config.maxDistanceError <= 0f)
            {
                return false;
            }

            return distance > config.maxDistanceError * Mathf.Max(0.1f, config.candidateDistanceMultiplier);
        }

        private static Texture2D BuildPsdComposite(PSDData psdData, List<BindingPairViewModel> bindings, int scale)
        {
            int width = Mathf.Max(1, psdData.width * scale);
            int height = Mathf.Max(1, psdData.height * scale);
            Texture2D canvas = new Texture2D(width, height, TextureFormat.RGBA32, false);
            Color32[] pixels = new Color32[width * height];
            Color32 bg = new Color32(30, 30, 30, 255);
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = bg;
            }
            canvas.SetPixels32(pixels);

            foreach (BindingPairViewModel bind in bindings.OrderByDescending(b => b.psdItem.index))
            {
                PicData item = bind.psdItem;
                if (item.isText || string.Equals(item.uiType, "Text", StringComparison.OrdinalIgnoreCase))
                {
                    PSDMatchGeometry.PsdGeom textGeom = PSDMatchGeometry.BuildPsdGeom(item, psdData.width, psdData.height);
                    DrawRectOutline(canvas, ToPixelRect(ToRectLog(textGeom, psdData.width, psdData.height, scale)), new Color32(220, 220, 220, 180), 1);
                    continue;
                }

                Texture2D layerTex = LoadPngTexture(psdData, item);
                if (layerTex == null)
                {
                    continue;
                }

                DrawLayer(canvas, layerTex, item, psdData.width, psdData.height, scale);
                UnityObject.DestroyImmediate(layerTex);
            }

            canvas.Apply();
            return canvas;
        }

        private static Texture2D LoadTemplateTexture(PSDData psdData, int scale, out string templateSourcePath)
        {
            templateSourcePath = ResolveTemplateAssetPath(psdData);
            if (string.IsNullOrEmpty(templateSourcePath))
            {
                return null;
            }

            Texture2D tex = LoadTextureFromPath(templateSourcePath);
            if (tex == null)
            {
                templateSourcePath = string.Empty;
                return null;
            }

            int targetWidth = Mathf.Max(1, psdData.width * scale);
            int targetHeight = Mathf.Max(1, psdData.height * scale);
            if (tex.width == targetWidth && tex.height == targetHeight)
            {
                return tex;
            }

            Texture2D resized = ResizeTexture(tex, targetWidth, targetHeight);
            UnityObject.DestroyImmediate(tex);
            return resized;
        }

        private static string ResolveTemplateAssetPath(PSDData psdData)
        {
            if (psdData == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrEmpty(psdData.templatePngPath) && File.Exists(ToAbsolutePath(psdData.templatePngPath)))
            {
                return psdData.templatePngPath;
            }

            string folder = (psdData.psdAssetsFolder ?? string.Empty).Replace("\\", "/").TrimEnd('/');
            string fallback = $"{folder}/template.png";
            return File.Exists(ToAbsolutePath(fallback)) ? fallback : string.Empty;
        }

        private static Texture2D LoadTextureFromPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            string absPath = ToAbsolutePath(path);
            if (!File.Exists(absPath))
            {
                return null;
            }

            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(File.ReadAllBytes(absPath)))
            {
                UnityObject.DestroyImmediate(tex);
                return null;
            }

            return tex;
        }

        private static Texture2D ResizeTexture(Texture2D source, int width, int height)
        {
            Texture2D resized = new Texture2D(width, height, TextureFormat.RGBA32, false);
            for (int y = 0; y < height; y++)
            {
                float v = height <= 1 ? 0f : y / (float)(height - 1);
                for (int x = 0; x < width; x++)
                {
                    float u = width <= 1 ? 0f : x / (float)(width - 1);
                    resized.SetPixel(x, y, source.GetPixelBilinear(u, v));
                }
            }

            resized.Apply();
            return resized;
        }

        private static ImageCompareLog BuildImageCompareLog(Texture2D imageA, Texture2D imageB, string pathA, string pathB)
        {
            const float differentPixelThreshold = 0.08f;
            var log = new ImageCompareLog
            {
                available = false,
                imageA = pathA,
                imageB = pathB,
                differentPixelThreshold = differentPixelThreshold
            };

            if (imageA == null || imageB == null)
            {
                log.note = "template image unavailable";
                return log;
            }

            int width = Mathf.Min(imageA.width, imageB.width);
            int height = Mathf.Min(imageA.height, imageB.height);
            if (width <= 0 || height <= 0)
            {
                log.note = "empty image";
                return log;
            }

            Color32[] pixelsA = imageA.GetPixels32();
            Color32[] pixelsB = imageB.GetPixels32();
            double totalDiff = 0d;
            int differentPixels = 0;
            for (int y = 0; y < height; y++)
            {
                int rowA = y * imageA.width;
                int rowB = y * imageB.width;
                for (int x = 0; x < width; x++)
                {
                    Color32 a = pixelsA[rowA + x];
                    Color32 b = pixelsB[rowB + x];
                    float diff = (Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b)) / (255f * 3f);
                    totalDiff += diff;
                    if (diff >= differentPixelThreshold)
                    {
                        differentPixels++;
                    }
                }
            }

            int comparedPixels = width * height;
            log.available = true;
            log.width = width;
            log.height = height;
            log.comparedPixels = comparedPixels;
            log.meanAbsoluteRgbDiff = Round3((float)(totalDiff / Math.Max(1, comparedPixels)));
            log.differentPixelRatio = Round3(differentPixels / (float)Math.Max(1, comparedPixels));
            if (imageA.width != imageB.width || imageA.height != imageB.height)
            {
                log.note = $"compared common area; A={imageA.width}x{imageA.height}, B={imageB.width}x{imageB.height}";
            }

            return log;
        }

        private static Texture2D BuildSuspectImage(Texture2D fullPsd, AuditLayer suspect, int scale)
        {
            Texture2D marked = CloneTexture(fullPsd);
            DrawRectOutline(marked, ToPixelRect(suspect.psdRect), PsdColor, 3);
            if (suspect.matchedRect != null)
            {
                DrawRectOutline(marked, ToPixelRect(suspect.matchedRect), MatchedColor, 3);
            }

            for (int i = 0; i < suspect.topCandidates.Count; i++)
            {
                Color32 color = CandidateColors[Mathf.Min(i, CandidateColors.Length - 1)];
                DrawRectOutline(marked, ToPixelRect(suspect.topCandidates[i].rect), color, 2);
            }

            marked.Apply();
            RectInt cropRect = GetSuspectCropRect(marked.width, marked.height, suspect);
            Texture2D crop = CropTexture(marked, cropRect);
            UnityObject.DestroyImmediate(marked);
            return crop;
        }

        private static Texture2D LoadPngTexture(PSDData psdData, PicData item)
        {
            string assetPath = GetPngAssetPath(psdData, item);
            if (string.IsNullOrEmpty(assetPath))
            {
                return null;
            }

            string absPath = ToAbsolutePath(assetPath);
            if (!File.Exists(absPath))
            {
                return null;
            }

            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(File.ReadAllBytes(absPath)))
            {
                UnityObject.DestroyImmediate(tex);
                return null;
            }

            return tex;
        }

        private static string GetPngAssetPath(PSDData psdData, PicData item)
        {
            string group = item.groupName == "root/" ? "/" : item.groupName;
            string folder = psdData.psdAssetsFolder.Replace("\\", "/");
            string pngPath = $"{folder}{group}{item.pngName}.png".Replace("//", "/");
            if (File.Exists(ToAbsolutePath(pngPath)))
            {
                return pngPath;
            }

            return PSDCreateor.FindPngByFilename(item.pngName, psdData.psdAssetsFolder);
        }

        private static void DrawLayer(Texture2D canvas, Texture2D src, PicData item, int psdWidth, int psdHeight, int scale)
        {
            PSDMatchGeometry.PsdGeom geom = PSDMatchGeometry.BuildPsdGeom(item, psdWidth, psdHeight);
            RectInt rect = ToPixelRect(ToRectLog(geom, psdWidth, psdHeight, scale));
            rect = ClampRect(rect, canvas.width, canvas.height);
            if (rect.width <= 0 || rect.height <= 0)
            {
                return;
            }

            for (int y = 0; y < rect.height; y++)
            {
                float v = rect.height <= 1 ? 0f : y / (float)(rect.height - 1);
                for (int x = 0; x < rect.width; x++)
                {
                    float u = rect.width <= 1 ? 0f : x / (float)(rect.width - 1);
                    Color srcColor = src.GetPixelBilinear(u, v);
                    if (srcColor.a <= 0.001f)
                    {
                        continue;
                    }

                    int dstX = rect.x + x;
                    int dstY = rect.y + y;
                    Color dst = canvas.GetPixel(dstX, dstY);
                    Color blended = Color.Lerp(dst, srcColor, srcColor.a);
                    blended.a = 1f;
                    canvas.SetPixel(dstX, dstY, blended);
                }
            }
        }

        private static void DrawRectOutline(Texture2D tex, RectInt rect, Color32 color, int thickness)
        {
            rect = ClampRect(rect, tex.width, tex.height);
            if (rect.width <= 0 || rect.height <= 0)
            {
                return;
            }

            thickness = Mathf.Max(1, thickness);
            for (int t = 0; t < thickness; t++)
            {
                int left = rect.x + t;
                int right = rect.x + rect.width - 1 - t;
                int bottom = rect.y + t;
                int top = rect.y + rect.height - 1 - t;
                for (int x = left; x <= right; x++)
                {
                    SetPixelSafe(tex, x, bottom, color);
                    SetPixelSafe(tex, x, top, color);
                }

                for (int y = bottom; y <= top; y++)
                {
                    SetPixelSafe(tex, left, y, color);
                    SetPixelSafe(tex, right, y, color);
                }
            }
        }

        private static void SetPixelSafe(Texture2D tex, int x, int y, Color32 color)
        {
            if (x < 0 || y < 0 || x >= tex.width || y >= tex.height)
            {
                return;
            }

            tex.SetPixel(x, y, color);
        }

        private static RectLog ToRectLog(PSDMatchGeometry.PsdGeom geom, int canvasWidth, int canvasHeight, int scale)
        {
            return ToRectLog(geom.centerLocal, geom.sizeLocal, geom.rectMinLocal, geom.rectMaxLocal, canvasWidth, canvasHeight, scale);
        }

        private static RectLog ToRectLog(PSDMatchGeometry.NodeGeom geom, int canvasWidth, int canvasHeight, int scale)
        {
            return ToRectLog(geom.centerLocal, geom.sizeLocal, geom.rectMinLocal, geom.rectMaxLocal, canvasWidth, canvasHeight, scale);
        }

        private static RectLog ToRectLog(Vector2 center, Vector2 size, Vector2 min, Vector2 max, int canvasWidth, int canvasHeight, int scale)
        {
            int pixelX = Mathf.RoundToInt((min.x + canvasWidth * 0.5f) * scale);
            int pixelY = Mathf.RoundToInt((min.y + canvasHeight * 0.5f) * scale);
            int pixelW = Mathf.RoundToInt(size.x * scale);
            int pixelH = Mathf.RoundToInt(size.y * scale);
            return new RectLog
            {
                centerX = Round1(center.x),
                centerY = Round1(center.y),
                width = Round1(size.x),
                height = Round1(size.y),
                rectMinX = Round1(min.x),
                rectMinY = Round1(min.y),
                rectMaxX = Round1(max.x),
                rectMaxY = Round1(max.y),
                pixelX = pixelX,
                pixelY = pixelY,
                pixelW = Mathf.Max(1, pixelW),
                pixelH = Mathf.Max(1, pixelH)
            };
        }

        private static RectInt ToPixelRect(RectLog rect)
        {
            return new RectInt(rect.pixelX, rect.pixelY, rect.pixelW, rect.pixelH);
        }

        private static RectInt ClampRect(RectInt rect, int width, int height)
        {
            int xMin = Mathf.Clamp(rect.xMin, 0, width);
            int yMin = Mathf.Clamp(rect.yMin, 0, height);
            int xMax = Mathf.Clamp(rect.xMax, 0, width);
            int yMax = Mathf.Clamp(rect.yMax, 0, height);
            return new RectInt(xMin, yMin, Mathf.Max(0, xMax - xMin), Mathf.Max(0, yMax - yMin));
        }

        private static RectInt GetSuspectCropRect(int width, int height, AuditLayer suspect)
        {
            RectInt rect = ToPixelRect(suspect.psdRect);
            if (suspect.matchedRect != null)
            {
                rect = Union(rect, ToPixelRect(suspect.matchedRect));
            }

            for (int i = 0; i < suspect.topCandidates.Count; i++)
            {
                rect = Union(rect, ToPixelRect(suspect.topCandidates[i].rect));
            }

            rect.x -= SuspectCropPadding;
            rect.y -= SuspectCropPadding;
            rect.width += SuspectCropPadding * 2;
            rect.height += SuspectCropPadding * 2;
            return ClampRect(rect, width, height);
        }

        private static RectInt Union(RectInt a, RectInt b)
        {
            int xMin = Mathf.Min(a.xMin, b.xMin);
            int yMin = Mathf.Min(a.yMin, b.yMin);
            int xMax = Mathf.Max(a.xMax, b.xMax);
            int yMax = Mathf.Max(a.yMax, b.yMax);
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        private static Texture2D CropTexture(Texture2D source, RectInt rect)
        {
            rect = ClampRect(rect, source.width, source.height);
            Texture2D crop = new Texture2D(Mathf.Max(1, rect.width), Mathf.Max(1, rect.height), TextureFormat.RGBA32, false);
            Color[] colors = source.GetPixels(rect.x, rect.y, Mathf.Max(1, rect.width), Mathf.Max(1, rect.height));
            crop.SetPixels(colors);
            crop.Apply();
            return crop;
        }

        private static Texture2D CloneTexture(Texture2D source)
        {
            Texture2D clone = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            clone.SetPixels32(source.GetPixels32());
            clone.Apply();
            return clone;
        }

        private static float CalculateIou(Vector2 aMin, Vector2 aMax, Vector2 bMin, Vector2 bMax)
        {
            float ixMin = Mathf.Max(aMin.x, bMin.x);
            float iyMin = Mathf.Max(aMin.y, bMin.y);
            float ixMax = Mathf.Min(aMax.x, bMax.x);
            float iyMax = Mathf.Min(aMax.y, bMax.y);
            float iw = Mathf.Max(0f, ixMax - ixMin);
            float ih = Mathf.Max(0f, iyMax - iyMin);
            float intersection = iw * ih;
            float aArea = Mathf.Max(0f, aMax.x - aMin.x) * Mathf.Max(0f, aMax.y - aMin.y);
            float bArea = Mathf.Max(0f, bMax.x - bMin.x) * Mathf.Max(0f, bMax.y - bMin.y);
            float union = aArea + bArea - intersection;
            return union > 0f ? intersection / union : 0f;
        }

        private static float CalculateMaxSizeErrorRatio(Vector2 expected, Vector2 actual)
        {
            float relW = Mathf.Abs(actual.x - expected.x) / Mathf.Max(1f, Mathf.Abs(expected.x));
            float relH = Mathf.Abs(actual.y - expected.y) / Mathf.Max(1f, Mathf.Abs(expected.y));
            return Mathf.Max(relW, relH);
        }

        private static List<BindingPairViewModel> BuildBindings(PSDData psdData)
        {
            var bindings = new List<BindingPairViewModel>();
            if (psdData == null || psdData.listPngData == null)
            {
                return bindings;
            }

            for (int i = 0; i < psdData.listPngData.Count; i++)
            {
                PicData item = psdData.listPngData[i];
                if (item.excludeFromRestore)
                {
                    continue;
                }

                bindings.Add(new BindingPairViewModel
                {
                    psdItem = item,
                    unityNode = null,
                    score = 0f,
                    isConfirmed = false,
                    statusInfo = "Waiting for match",
                    isIdMatched = false,
                    isPreLocked = false,
                    depth = 0,
                    bestCandidateScore = 0f,
                    secondBestCandidateScore = 0f,
                    scoreMargin = 0f,
                    isLowConfidence = false,
                    skippedTypeCandidates = 0,
                    skippedSpatialCandidates = 0,
                    hierarchyPenalizedCandidates = 0
                });
            }

            return bindings;
        }

        private static PSDBindingData LoadBindingAsset(string psdPath, bool createIfMissing)
        {
            string dir = Path.GetDirectoryName(psdPath);
            string name = Path.GetFileNameWithoutExtension(psdPath) + "_Binding.asset";
            string assetPath = NormalizeAssetPath(Path.Combine(dir ?? string.Empty, name));
            PSDBindingData binding = AssetDatabase.LoadAssetAtPath<PSDBindingData>(assetPath);
            if (binding == null && createIfMissing)
            {
                binding = ScriptableObject.CreateInstance<PSDBindingData>();
                AssetDatabase.CreateAsset(binding, assetPath);
                AssetDatabase.SaveAssets();
            }

            if (binding != null)
            {
                binding.BuildCache();
            }

            return binding;
        }

        private static PSDMatchAuditConfig GetActiveOrDefaultConfig()
        {
            if (Selection.activeObject is PSDMatchAuditConfig selected)
            {
                return selected;
            }

            return GetOrCreateDefaultConfig();
        }

        private static PSDMatchAuditConfig GetOrCreateDefaultConfig()
        {
            PSDMatchAuditConfig config = AssetDatabase.LoadAssetAtPath<PSDMatchAuditConfig>(DefaultConfigPath);
            if (config != null)
            {
                return config;
            }

            EnsureAssetFolder(Path.GetDirectoryName(DefaultConfigPath));
            config = ScriptableObject.CreateInstance<PSDMatchAuditConfig>();
            config.importConfig = FindFirstAsset<PSDImportConfig>();
            config.outputFolder = DefaultOutputFolder;
            AssetDatabase.CreateAsset(config, DefaultConfigPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return config;
        }

        private static string ResolvePsdDataPath(PSDMatchAuditConfig config)
        {
            if (config.psdDataAsset != null)
            {
                string path = AssetDatabase.GetAssetPath(config.psdDataAsset);
                if (!string.IsNullOrEmpty(path) && path.EndsWith(".ps.data", StringComparison.OrdinalIgnoreCase))
                {
                    return NormalizeAssetPath(path);
                }
            }

            if (!string.IsNullOrEmpty(config.psdDataPath))
            {
                return NormalizeAssetPath(config.psdDataPath);
            }

            if (Selection.activeObject != null)
            {
                string path = AssetDatabase.GetAssetPath(Selection.activeObject);
                if (!string.IsNullOrEmpty(path) && path.EndsWith(".ps.data", StringComparison.OrdinalIgnoreCase))
                {
                    return NormalizeAssetPath(path);
                }
            }

            return FindFirstPsDataPath();
        }

        private static GameObject ResolveTargetRoot(PSDMatchAuditConfig config)
        {
            if (config.targetRoot != null)
            {
                return config.targetRoot;
            }

            if (!string.IsNullOrEmpty(config.targetRootPath))
            {
                GameObject byPath = GameObject.Find(config.targetRootPath);
                if (byPath != null)
                {
                    return byPath;
                }

                GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(NormalizeAssetPath(config.targetRootPath));
                if (asset != null)
                {
                    return asset;
                }
            }

            if (Selection.activeGameObject != null)
            {
                return Selection.activeGameObject;
            }

            return FindFirstSceneRectRoot();
        }

        private static string FindFirstPsDataPath()
        {
            string root = Path.Combine(Application.dataPath, "_OpenCode/TZUI/PS").Replace("\\", "/");
            if (!Directory.Exists(root))
            {
                return null;
            }

            string file = Directory.GetFiles(root, "*.ps.data", SearchOption.AllDirectories).FirstOrDefault();
            return string.IsNullOrEmpty(file) ? null : ToAssetPath(file);
        }

        private static GameObject FindFirstSceneRectRoot()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                return null;
            }

            GameObject[] roots = scene.GetRootGameObjects();
            GameObject canvasRoot = roots.FirstOrDefault(r => r != null && r.GetComponent<Canvas>() != null && r.GetComponent<RectTransform>() != null);
            if (canvasRoot != null)
            {
                return canvasRoot;
            }

            return roots.FirstOrDefault(r => r != null && r.GetComponent<RectTransform>() != null);
        }

        private static T FindFirstAsset<T>() where T : UnityObject
        {
            string[] guids = AssetDatabase.FindAssets($"t:{typeof(T).Name}");
            if (guids == null || guids.Length == 0)
            {
                return null;
            }

            return AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        private static bool IsPrefabAsset(GameObject go, out string assetPath)
        {
            assetPath = go != null ? AssetDatabase.GetAssetPath(go) : null;
            return go != null &&
                   !string.IsNullOrEmpty(assetPath) &&
                   AssetDatabase.LoadAssetAtPath<GameObject>(assetPath) == go &&
                   PrefabUtility.GetPrefabAssetType(go) != PrefabAssetType.NotAPrefab;
        }

        private static string GetMatchMethod(BindingPairViewModel bind)
        {
            if (bind.isIdMatched) return "ID_History";
            if (bind.isPreLocked) return "PreLock";
            if (bind.isAutoCreated) return "AutoCreated";
            if (bind.unityNode != null) return "Hungarian";
            return "Unmatched";
        }

        private static void AddReason(AuditLayer layer, string reason, string causeTag)
        {
            if (!layer.reasons.Contains(reason))
            {
                layer.reasons.Add(reason);
            }

            if (!layer.causeTags.Contains(causeTag))
            {
                layer.causeTags.Add(causeTag);
            }
        }

        private static AgentReviewTemplate BuildReviewTemplate(List<AuditLayer> suspects, string sourceSuspectsJson)
        {
            var template = new AgentReviewTemplate
            {
                generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                sourceSuspectsJson = sourceSuspectsJson
            };

            for (int i = 0; i < suspects.Count; i++)
            {
                template.reviews.Add(new AgentReviewItem
                {
                    layerId = suspects[i].layerId,
                    pngName = suspects[i].pngName,
                    verdict = "uncertain",
                    correctNodePath = suspects[i].matchedNodePath,
                    causeTag = suspects[i].causeTags.Count > 0 ? suspects[i].causeTags[0] : "unknown",
                    notes = string.Empty
                });
            }

            return template;
        }

        private static AuditConfigLog BuildConfigLog(PSDImportConfig config)
        {
            if (config == null)
            {
                config = FindFirstAsset<PSDImportConfig>();
            }

            bool ownsTemp = false;
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<PSDImportConfig>();
                ownsTemp = true;
            }

            AuditConfigLog log = new AuditConfigLog
            {
                maxDistanceError = config.maxDistanceError,
                maxSizeDiff = config.maxSizeDiff,
                weightPosition = config.weightPosition,
                weightSize = config.weightSize,
                weightType = config.weightType,
                weightDepth = config.weightDepth,
                weightAnchor = config.weightAnchor,
                maxDepthDiff = config.maxDepthDiff,
                enableIdHistoryMatch = config.enableIdHistoryMatch,
                lowConfidenceMargin = config.lowConfidenceMargin,
                enableGeometryReject = config.enableGeometryReject,
                geometryRejectDistanceMultiplier = config.geometryRejectDistanceMultiplier,
                geometryRejectSizeRatio = config.geometryRejectSizeRatio,
                enableCandidatePruning = config.enableCandidatePruning,
                candidateDistanceMultiplier = config.candidateDistanceMultiplier,
                preLockEnabled = config.preLockEnabled,
                preLockUseDynamicThreshold = config.preLockUseDynamicThreshold,
                preLockDynamicRatio = config.preLockDynamicRatio,
                preLockDynamicMinThreshold = config.preLockDynamicMinThreshold,
                autoSlice = config.autoSlice,
                dedupeSprites = config.dedupeSprites,
                textLayoutPaddingX = config.textLayoutPaddingX,
                textLayoutPaddingY = config.textLayoutPaddingY,
                textPreserveLargerLayoutRect = config.textPreserveLargerLayoutRect,
                textSingleLineExpansionMaxRatio = config.textSingleLineExpansionMaxRatio,
                textSingleLineExpansionMaxExtraWidth = config.textSingleLineExpansionMaxExtraWidth,
                textSizeScoreWeightMultiplier = config.textSizeScoreWeightMultiplier,
                textGeometryRejectSizeRatio = config.textGeometryRejectSizeRatio
            };

            if (ownsTemp)
            {
                UnityObject.DestroyImmediate(config);
            }

            return log;
        }

        private static string GetTransformPath(Transform t, Transform root)
        {
            if (t == null)
            {
                return string.Empty;
            }

            if (root != null && t == root)
            {
                return ".";
            }

            List<string> parts = new List<string>();
            Transform current = t;
            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string GetTargetDisplayPath(GameObject target)
        {
            if (target == null)
            {
                return string.Empty;
            }

            string assetPath = AssetDatabase.GetAssetPath(target);
            return string.IsNullOrEmpty(assetPath) ? GetTransformPath(target.transform, null) : assetPath;
        }

        private static void WriteJson(string absPath, object obj)
        {
            File.WriteAllText(absPath, JsonConvert.SerializeObject(obj, Formatting.Indented), Encoding.UTF8);
        }

        private static void WriteTexture(Texture2D tex, string absPath)
        {
            File.WriteAllBytes(absPath, tex.EncodeToPNG());
        }

        private static void EnsureAssetFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            folder = NormalizeAssetPath(folder);
            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }

        private static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            path = path.Replace("\\", "/");
            int assetsIndex = path.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
            if (assetsIndex >= 0)
            {
                return path.Substring(assetsIndex);
            }

            return path;
        }

        private static string ToAbsolutePath(string path)
        {
            path = path.Replace("\\", "/");
            if (Path.IsPathRooted(path))
            {
                return path;
            }

            string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace("\\", "/");
            return Path.Combine(projectRoot, path).Replace("\\", "/");
        }

        private static string ToAssetPath(string absPath)
        {
            string path = absPath.Replace("\\", "/");
            int assetsIndex = path.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
            return assetsIndex >= 0 ? path.Substring(assetsIndex) : path;
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "unnamed";
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            return new string(chars);
        }

        private static string ToHex(Color32 color)
        {
            return $"#{color.r:X2}{color.g:X2}{color.b:X2}";
        }

        private static float Round1(float value)
        {
            return Mathf.Round(value * 10f) / 10f;
        }

        private static float Round3(float value)
        {
            return Mathf.Round(value * 1000f) / 1000f;
        }

        private static void CleanupTemp(UnityObject obj, bool owns)
        {
            if (owns && obj != null)
            {
                UnityObject.DestroyImmediate(obj);
            }
        }
    }
}
