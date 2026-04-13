using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PSDImporter
{
    public static class VisualBindingRestoreService
    {
        private struct ScoreBreakdown
        {
            public PSDMatchScoring.ScoreBreakdown core;
            public float mlProb;
            public bool mlUsed;

            public float distance => core.geometry.distance;
            public float diffW => core.geometry.diffW;
            public float diffH => core.geometry.diffH;
            public float scorePos => core.scorePos;
            public float scoreSize => core.scoreSize;
            public float scoreType => core.scoreType;
            public float weightedPos => core.weightedPos;
            public float weightedSize => core.weightedSize;
            public float weightedType => core.weightedType;
            public float total => core.total;
            public bool passThresholds => core.passThresholds;
        }

        private class MatchCandidate
        {
            public RectTransform node;
            public float score;
            public ScoreBreakdown breakdown;
        }

        /// <summary>
        /// Phase 3.5 pre-lock result: a high-confidence PSD↔Node pair locked before Hungarian.
        /// </summary>
        private struct PreLockedPair
        {
            public int bindIndex;       // index into pendingBinds
            public int nodeIndex;       // index into nodeList
            public float score;
            public bool isConfirmed;
            public string reason;
        }

        public static void RunAutoMatch(
            List<BindingPairViewModel> bindings,
            GameObject targetRoot,
            PSDData cachedPsdData,
            PSDBindingData bindingAsset,
            PSDImportConfig config)
        {
            if (targetRoot == null || cachedPsdData == null || bindings == null || bindings.Count == 0)
            {
                return;
            }

            PSDImportConfig matchConfig = config;
            bool ownsTempConfig = false;
            if (matchConfig == null)
            {
                matchConfig = ScriptableObject.CreateInstance<PSDImportConfig>();
                ownsTempConfig = true;
            }

            try
            {
                Transform rootTransform = targetRoot.transform;
                RectTransform rootRectTransform = rootTransform as RectTransform;
                if (rootRectTransform == null)
                {
                    Debug.LogWarning("[Match] targetRoot is not a RectTransform, skip auto match.");
                    return;
                }
                bool logDetail = matchConfig.showDetailedLog;
                bool forceCandidateLog = matchConfig.forceCandidateLog;
                bool logCandidatesInLoop = logDetail && !forceCandidateLog;

                // ML path is temporarily disabled. Always use manual weighted scoring.
                bool useMlScore = false;
                PSDMatchModel mlModel = null;

                float perfectThreshold = 150f;
                HashSet<Transform> occupiedNodes = new HashSet<Transform>();
                HashSet<BindingPairViewModel> matchedBindings = new HashSet<BindingPairViewModel>();

                if (logDetail)
                {
                    Debug.Log($"[Match] Config maxDist={matchConfig.maxDistanceError:F1}, maxSizeDiff={matchConfig.maxSizeDiff:F1}, weightPos={matchConfig.weightPosition:F2}, weightSize={matchConfig.weightSize:F2}, weightType={matchConfig.weightType:F2}, skipInactive={matchConfig.skipInactiveMatch}, allowUnmatched={matchConfig.allowUnmatched}, unmatchedPenalty={matchConfig.unmatchedPenalty:F1}, minAcceptScore={matchConfig.minAcceptScore:F1}, forceCandidateLog={forceCandidateLog}, useMlScore={useMlScore}");
                }

                foreach (var bind in bindings)
                {
                    if (bind.isConfirmed && bind.unityNode != null)
                    {
                        occupiedNodes.Add(bind.unityNode);
                        matchedBindings.Add(bind);
                    }
                    else
                    {
                        bind.unityNode = null;
                        bind.score = 0;
                        bind.statusInfo = "Waiting for match";
                        bind.isIdMatched = false;
                    }
                }

                List<BindingPairViewModel> pendingBinds = new List<BindingPairViewModel>();
                foreach (var bind in bindings)
                {
                    if (matchedBindings.Contains(bind))
                    {
                        continue;
                    }

                    if (bindingAsset != null)
                    {
                        GameObject savedGo = bindingAsset.GetBindTarget(bind.psdItem.id);
                        if (savedGo != null && savedGo.transform.IsChildOf(rootTransform) && !occupiedNodes.Contains(savedGo.transform))
                        {
                            bind.unityNode = savedGo.transform;
                            bind.score = 9999f;
                            bind.statusInfo = "ID history binding";
                            bind.isIdMatched = true;
                            bind.isConfirmed = true;
                            matchedBindings.Add(bind);
                            occupiedNodes.Add(savedGo.transform);

                            if (logDetail)
                            {
                                Debug.Log($"[Match] Saved binding for {bind.psdItem.pngName} -> {GetTransformPath(savedGo.transform)} score=9999 (ID)");
                            }
                            continue;
                        }
                    }

                    pendingBinds.Add(bind);
                }

                var allNodes = targetRoot.GetComponentsInChildren<RectTransform>(true);
                List<RectTransform> nodeList = new List<RectTransform>();
                List<RectTransform> nodeListForLog = forceCandidateLog ? new List<RectTransform>() : nodeList;

                int skippedRoot = 0;
                int skippedOccupied = 0;
                int skippedInactive = 0;
                int skippedRootLog = 0;
                int skippedInactiveLog = 0;
                int skippedOccupiedLog = 0;

                foreach (var node in allNodes)
                {
                    if (node == rootTransform)
                    {
                        skippedRoot++;
                        skippedRootLog++;
                        continue;
                    }

                    if (matchConfig.skipInactiveMatch && !node.gameObject.activeInHierarchy)
                    {
                        skippedInactive++;
                        skippedInactiveLog++;
                        continue;
                    }

                    bool isOccupied = occupiedNodes.Contains(node.transform);
                    if (isOccupied)
                    {
                        skippedOccupied++;
                    }
                    else
                    {
                        nodeList.Add(node);
                    }

                    if (forceCandidateLog)
                    {
                        if (isOccupied)
                        {
                            skippedOccupiedLog++;
                        }
                        nodeListForLog.Add(node);
                    }
                }

                if (logDetail)
                {
                    Debug.Log($"[Match] Candidate nodes={nodeList.Count}, skipped(root:{skippedRoot}, occupied:{skippedOccupied}, inactive:{skippedInactive})");
                    if (forceCandidateLog)
                    {
                        Debug.Log($"[Match] Candidate nodes(for log)={nodeListForLog.Count}, skipped(root:{skippedRootLog}, occupied:{skippedOccupiedLog}, inactive:{skippedInactiveLog})");
                    }
                }

                if (logDetail && forceCandidateLog)
                {
                    foreach (var bind in bindings)
                    {
                        LogTopCandidatesForBind(bind, nodeListForLog, rootTransform, cachedPsdData, matchConfig, useMlScore, mlModel, skippedRootLog, skippedInactiveLog, skippedOccupiedLog);
                    }
                }

                int itemCount = pendingBinds.Count;
                int nodeCount = nodeList.Count;
                if (itemCount == 0 || nodeCount == 0)
                {
                    foreach (var bind in pendingBinds)
                    {
                        bind.statusInfo = "No suitable node found";
                    }
                    return;
                }

                float[,] scoreMatrix = new float[itemCount, nodeCount];
                bool[,] validMatrix = new bool[itemCount, nodeCount];
                float maxScore = 0f;

                for (int i = 0; i < itemCount; i++)
                {
                    var bind = pendingBinds[i];
                    var psdGeom = PSDMatchGeometry.BuildPsdGeom(bind.psdItem, cachedPsdData.width, cachedPsdData.height);
                    List<MatchCandidate> localCandidates = logCandidatesInLoop ? new List<MatchCandidate>() : null;
                    int skippedType = 0;

                    for (int j = 0; j < nodeCount; j++)
                    {
                        var node = nodeList[j];
                        if (!IsTypeMatch(node, bind.psdItem.uiType))
                        {
                            skippedType++;
                            continue;
                        }

                        ScoreBreakdown breakdown;
                        float score = GetMatchScore(bind.psdItem, node, psdGeom, matchConfig, useMlScore, mlModel, rootRectTransform, cachedPsdData, out breakdown);
                        if (score > 1f)
                        {
                            validMatrix[i, j] = true;
                            scoreMatrix[i, j] = score;
                            if (score > maxScore)
                            {
                                maxScore = score;
                            }

                            if (logCandidatesInLoop)
                            {
                                localCandidates.Add(new MatchCandidate
                                {
                                    node = node,
                                    score = score,
                                    breakdown = breakdown
                                });
                            }
                        }
                    }

                    if (logCandidatesInLoop)
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine($"[Match] Item {bind.psdItem.pngName} (id:{bind.psdItem.id}, type:{bind.psdItem.uiType}) centerLocal=({psdGeom.centerLocal.x:F1},{psdGeom.centerLocal.y:F1}) size=({psdGeom.sizeLocal.x:F1},{psdGeom.sizeLocal.y:F1}) candidates={localCandidates.Count} skipped(type:{skippedType}, inactive:{skippedInactive}, occupied:{skippedOccupied}, root:{skippedRoot})");
                        var top = localCandidates.OrderByDescending(c => c.score).Take(5).ToList();
                        for (int k = 0; k < top.Count; k++)
                        {
                            var cand = top[k];
                            var b = cand.breakdown;
                            var nodeGeom = PSDMatchGeometry.ExtractNodeGeom(cand.node, rootRectTransform);
                            string mlInfo = b.mlUsed ? $" mlScore={cand.score:F1} mlProb={b.mlProb:F3}" : string.Empty;
                            string geomInfo = matchConfig.logMatchGeometry ? $" nodeCenterLocal=({nodeGeom.centerLocal.x:F1},{nodeGeom.centerLocal.y:F1}) nodeSizeLocal=({nodeGeom.sizeLocal.x:F1},{nodeGeom.sizeLocal.y:F1}) psdCenterLocal=({psdGeom.centerLocal.x:F1},{psdGeom.centerLocal.y:F1}) psdSizeLocal=({psdGeom.sizeLocal.x:F1},{psdGeom.sizeLocal.y:F1})" : string.Empty;
                            sb.AppendLine($"  #{k + 1} {GetTransformPath(cand.node)} active={cand.node.gameObject.activeInHierarchy}{geomInfo} dist={b.distance:F1} diff=({b.diffW:F1},{b.diffH:F1}) scorePos={b.scorePos:F1} scoreSize={b.scoreSize:F1} scoreType={b.scoreType:F0} w=({b.weightedPos:F1},{b.weightedSize:F1},{b.weightedType:F1}) total={b.total:F1}{mlInfo}");
                        }
                        Debug.Log(sb.ToString());
                    }
                }

                // ========================================================================
                // Phase 3.5: High-Confidence Pre-Lock (Layered Matching)
                // Lock exclusive-optimal match pairs BEFORE Hungarian to prevent
                // global-optimal misassignment where obvious pairs get split apart.
                // ========================================================================
                if (matchConfig.preLockEnabled && pendingBinds.Count > 0 && nodeList.Count > 0)
                {
                    var preLocked = ApplyHighConfidencePreLock(
                        pendingBinds,
                        nodeList,
                        scoreMatrix,
                        validMatrix,
                        matchConfig.preLockThreshold,
                        matchConfig.preLockColumnUniquenessRatio,
                        perfectThreshold,
                        occupiedNodes,
                        matchedBindings,
                        logDetail);

                    if (preLocked.Count > 0)
                    {
                        // Rebuild pendingBinds and nodeList excluding locked pairs.
                        // Use descending index order to avoid shifting issues.
                        var lockedBindIndices = new HashSet<int>(preLocked.Select(p => p.bindIndex));
                        var lockedNodeIndices = new HashSet<int>(preLocked.Select(p => p.nodeIndex));

                        var remainingBinds = new List<BindingPairViewModel>();
                        for (int i = 0; i < pendingBinds.Count; i++)
                        {
                            if (!lockedBindIndices.Contains(i))
                                remainingBinds.Add(pendingBinds[i]);
                        }

                        var remainingNodes = new List<RectTransform>();
                        for (int j = 0; j < nodeList.Count; j++)
                        {
                            if (!lockedNodeIndices.Contains(j))
                                remainingNodes.Add(nodeList[j]);
                        }

                        // If everything was locked, skip Hungarian entirely
                        if (remainingBinds.Count == 0 || remainingNodes.Count == 0)
                        {
                            if (logDetail)
                            {
                                Debug.Log($"[Match] All {pendingBinds.Count} pairs resolved by pre-lock, skipping Hungarian.");
                            }
                            // Early exit — all bindings are already set
                            int unmatchedFinal = bindings.Count(b => b.unityNode == null);
                            float unmatchedRateFinal = bindings.Count > 0 ? (float)unmatchedFinal / bindings.Count : 0f;
                            Debug.Log($"[Match] Unmatched rate: {unmatchedFinal}/{bindings.Count} ({unmatchedRateFinal:P1})");
                            return;
                        }

                        // Build remapping tables for score/valid matrix indices
                        int[] bindRemap = new int[remainingBinds.Count];
                        int[] nodeRemap = new int[remainingNodes.Count];
                        int bi = 0, ni = 0;
                        for (int i = 0; i < pendingBinds.Count; i++)
                        {
                            if (!lockedBindIndices.Contains(i)) bindRemap[bi++] = i;
                        }
                        for (int j = 0; j < nodeList.Count; j++)
                        {
                            if (!lockedNodeIndices.Contains(j)) nodeRemap[ni++] = j;
                        }

                        // Shrink matrices
                        float[,] shrunkScore = new float[remainingBinds.Count, remainingNodes.Count];
                        bool[,] shrunkValid = new bool[remainingBinds.Count, remainingNodes.Count];
                        maxScore = 0f;

                        for (int i = 0; i < remainingBinds.Count; i++)
                        {
                            for (int j = 0; j < remainingNodes.Count; j++)
                            {
                                int origI = bindRemap[i];
                                int origJ = nodeRemap[j];
                                shrunkScore[i, j] = scoreMatrix[origI, origJ];
                                shrunkValid[i, j] = validMatrix[origI, origJ];
                                if (shrunkValid[i, j] && shrunkScore[i, j] > maxScore)
                                    maxScore = shrunkScore[i, j];
                            }
                        }

                        pendingBinds = remainingBinds;
                        nodeList = remainingNodes;
                        scoreMatrix = shrunkScore;
                        validMatrix = shrunkValid;

                        itemCount = pendingBinds.Count;
                        nodeCount = nodeList.Count;

                        if (logDetail)
                        {
                            Debug.Log($"[Match] After pre-lock: {itemCount} pending binds, {nodeCount} candidate nodes remain");
                        }
                    }
                }

                if (maxScore <= 0f && !matchConfig.allowUnmatched)
                {
                    foreach (var bind in pendingBinds)
                    {
                        bind.statusInfo = "No suitable node found";
                    }
                    return;
                }

                int dummyCount = matchConfig.allowUnmatched ? itemCount : 0;
                int totalColumns = nodeCount + dummyCount;
                int size = Mathf.Max(itemCount, totalColumns);
                float referenceScore = Mathf.Max(maxScore, matchConfig.unmatchedPenalty);
                float invalidCost = referenceScore + 1000f;
                float dummyCost = Mathf.Max(0f, referenceScore - matchConfig.unmatchedPenalty);

                float[,] cost = new float[size, size];
                for (int i = 0; i < size; i++)
                {
                    for (int j = 0; j < size; j++)
                    {
                        cost[i, j] = invalidCost;
                    }
                }

                for (int i = 0; i < itemCount; i++)
                {
                    for (int j = 0; j < nodeCount; j++)
                    {
                        if (validMatrix[i, j])
                        {
                            cost[i, j] = referenceScore - scoreMatrix[i, j];
                        }
                    }

                    if (matchConfig.allowUnmatched)
                    {
                        for (int d = 0; d < dummyCount; d++)
                        {
                            cost[i, nodeCount + d] = dummyCost;
                        }
                    }
                }

                int[] assignment = PSDHungarianSolver.Solve(cost);
                for (int i = 0; i < itemCount; i++)
                {
                    var bind = pendingBinds[i];
                    int j = assignment[i];
                    bool isDummyAssignment = j >= nodeCount && j < totalColumns;

                    if (j < 0 || j >= totalColumns)
                    {
                        bind.unityNode = null;
                        bind.score = 0f;
                        bind.isConfirmed = false;
                        bind.statusInfo = "Unmatched: invalid assignment";
                        continue;
                    }

                    if (isDummyAssignment)
                    {
                        bind.unityNode = null;
                        bind.score = 0f;
                        bind.isConfirmed = false;
                        bind.statusInfo = "Unmatched: assigned to dummy";
                        continue;
                    }

                    if (!validMatrix[i, j])
                    {
                        bind.unityNode = null;
                        bind.score = 0f;
                        bind.isConfirmed = false;
                        bind.statusInfo = "Unmatched: invalid candidate";
                        continue;
                    }

                    var node = nodeList[j];
                    if (occupiedNodes.Contains(node.transform))
                    {
                        bind.unityNode = null;
                        bind.score = 0f;
                        bind.isConfirmed = false;
                        bind.statusInfo = "Unmatched: node already occupied";
                        continue;
                    }

                    bind.unityNode = node;
                    bind.score = scoreMatrix[i, j];
                    bind.isIdMatched = false;

                    if (matchConfig.allowUnmatched && bind.score < matchConfig.minAcceptScore)
                    {
                        bind.unityNode = null;
                        bind.isConfirmed = false;
                        bind.statusInfo = $"Unmatched: score {bind.score:F1} < minAcceptScore {matchConfig.minAcceptScore:F1}";
                        continue;
                    }

                    bind.statusInfo = $"Score: {bind.score:F0}";
                    if (bind.score > perfectThreshold)
                    {
                        bind.isConfirmed = true;
                    }

                    matchedBindings.Add(bind);
                    occupiedNodes.Add(node.transform);

                    if (logDetail)
                    {
                        ScoreBreakdown breakdown;
                        var psdGeom = PSDMatchGeometry.BuildPsdGeom(bind.psdItem, cachedPsdData.width, cachedPsdData.height);
                        GetMatchScore(bind.psdItem, node, psdGeom, matchConfig, useMlScore, mlModel, rootRectTransform, cachedPsdData, out breakdown);
                        var nodeGeom = PSDMatchGeometry.ExtractNodeGeom(node, rootRectTransform);
                        string mlInfo = breakdown.mlUsed ? $" mlScore={bind.score:F1} mlProb={breakdown.mlProb:F3}" : string.Empty;
                        string geomInfo = matchConfig.logMatchGeometry ? $" nodeCenterLocal=({nodeGeom.centerLocal.x:F1},{nodeGeom.centerLocal.y:F1}) nodeSizeLocal=({nodeGeom.sizeLocal.x:F1},{nodeGeom.sizeLocal.y:F1}) psdCenterLocal=({psdGeom.centerLocal.x:F1},{psdGeom.centerLocal.y:F1}) psdSizeLocal=({psdGeom.sizeLocal.x:F1},{psdGeom.sizeLocal.y:F1})" : string.Empty;
                        Debug.Log($"[Match] Result {bind.psdItem.pngName} -> {GetTransformPath(node)} score={bind.score:F1} dist={breakdown.distance:F1} diff=({breakdown.diffW:F1},{breakdown.diffH:F1}){geomInfo} w=({breakdown.weightedPos:F1},{breakdown.weightedSize:F1},{breakdown.weightedType:F1}){mlInfo}");
                    }
                }

                foreach (var bind in pendingBinds)
                {
                    if (!matchedBindings.Contains(bind) && bind.unityNode == null && string.IsNullOrEmpty(bind.statusInfo))
                    {
                        bind.statusInfo = "No suitable node found";
                    }
                }

                int unmatchedCount = bindings.Count(b => b.unityNode == null);
                float unmatchedRate = bindings.Count > 0 ? (float)unmatchedCount / bindings.Count : 0f;
                Debug.Log($"[Match] Unmatched rate: {unmatchedCount}/{bindings.Count} ({unmatchedRate:P1})");

                // Export detailed match log to JSON when showDetailedLog is enabled
                if (logDetail)
                {
                    PSDMatchLogExporter.Export(bindings, targetRoot, cachedPsdData, matchConfig);
                }
            }
            finally
            {
                if (ownsTempConfig)
                {
                    Object.DestroyImmediate(matchConfig);
                }
            }
        }

        public static int ApplyBindings(
            List<BindingPairViewModel> bindings,
            GameObject targetRoot,
            PSDData cachedPsdData,
            PSDBindingData bindingAsset,
            PSDImportConfig config,
            string psdPath)
        {
            if (targetRoot == null || cachedPsdData == null || bindings == null)
            {
                return 0;
            }

            Undo.RegisterFullObjectHierarchyUndo(targetRoot, "Apply Visual Bindings");

            int count = 0;
            for (int i = 0; i < bindings.Count; i++)
            {
                var bind = bindings[i];
                if (bind.unityNode == null)
                {
                    continue;
                }

                PSDCreateor.RefreshNode(bind.unityNode.gameObject, bind.psdItem, cachedPsdData, true, config);
                if (bindingAsset != null && bind.isConfirmed)
                {
                    bindingAsset.SaveBinding(bind.psdItem.id, bind.psdItem.pngName, bind.unityNode.gameObject);
                }
                count++;
            }

            if (bindingAsset != null)
            {
                EditorUtility.SetDirty(bindingAsset);
            }
            AssetDatabase.SaveAssets();

            // ML auto learn is temporarily disabled.

            return count;
        }

        private static void LogTopCandidatesForBind(
            BindingPairViewModel bind,
            IList<RectTransform> nodes,
            Transform rootTransform,
            PSDData cachedPsdData,
            PSDImportConfig config,
            bool useMlScore,
            PSDMatchModel mlModel,
            int skippedRoot,
            int skippedInactive,
            int skippedOccupied)
        {
            if (bind == null || nodes == null || config == null)
            {
                return;
            }

            RectTransform rootRect = rootTransform as RectTransform;
            if (rootRect == null)
            {
                return;
            }
            var psdGeom = PSDMatchGeometry.BuildPsdGeom(bind.psdItem, cachedPsdData.width, cachedPsdData.height);

            List<MatchCandidate> localCandidates = new List<MatchCandidate>();
            int skippedType = 0;
            for (int j = 0; j < nodes.Count; j++)
            {
                var node = nodes[j];
                if (!IsTypeMatch(node, bind.psdItem.uiType))
                {
                    skippedType++;
                    continue;
                }

                ScoreBreakdown breakdown;
                float score = GetMatchScore(bind.psdItem, node, psdGeom, config, useMlScore, mlModel, rootRect, cachedPsdData, out breakdown);
                if (score > 1f)
                {
                    localCandidates.Add(new MatchCandidate
                    {
                        node = node,
                        score = score,
                        breakdown = breakdown
                    });
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[Match] Item {bind.psdItem.pngName} (id:{bind.psdItem.id}, type:{bind.psdItem.uiType}) centerLocal=({psdGeom.centerLocal.x:F1},{psdGeom.centerLocal.y:F1}) size=({psdGeom.sizeLocal.x:F1},{psdGeom.sizeLocal.y:F1}) candidates={localCandidates.Count} skipped(type:{skippedType}, inactive:{skippedInactive}, occupied:{skippedOccupied}, root:{skippedRoot})");
            var top = localCandidates.OrderByDescending(c => c.score).Take(5).ToList();
            for (int k = 0; k < top.Count; k++)
            {
                var cand = top[k];
                var b = cand.breakdown;
                var nodeGeom = PSDMatchGeometry.ExtractNodeGeom(cand.node, rootRect);
                string mlInfo = b.mlUsed ? $" mlScore={cand.score:F1} mlProb={b.mlProb:F3}" : string.Empty;
                string geomInfo = config.logMatchGeometry ? $" nodeCenterLocal=({nodeGeom.centerLocal.x:F1},{nodeGeom.centerLocal.y:F1}) nodeSizeLocal=({nodeGeom.sizeLocal.x:F1},{nodeGeom.sizeLocal.y:F1}) psdCenterLocal=({psdGeom.centerLocal.x:F1},{psdGeom.centerLocal.y:F1}) psdSizeLocal=({psdGeom.sizeLocal.x:F1},{psdGeom.sizeLocal.y:F1})" : string.Empty;
                sb.AppendLine($"  #{k + 1} {GetTransformPath(cand.node)} active={cand.node.gameObject.activeInHierarchy}{geomInfo} dist={b.distance:F1} diff=({b.diffW:F1},{b.diffH:F1}) scorePos={b.scorePos:F1} scoreSize={b.scoreSize:F1} scoreType={b.scoreType:F0} w=({b.weightedPos:F1},{b.weightedSize:F1},{b.weightedType:F1}) total={b.total:F1}{mlInfo}");
            }
            Debug.Log(sb.ToString());
        }

        private static float CalculateMatchScoreDetailed(PicData item, RectTransform node, PSDMatchGeometry.PsdGeom psdGeom, RectTransform root, PSDImportConfig config, out ScoreBreakdown breakdown)
        {
            breakdown = new ScoreBreakdown();
            // PicData is a struct, use default comparison for value type null check
            if (config == null || item.Equals(default) || node == null || root == null)
            {
                return 0f;
            }

            var nodeGeom = PSDMatchGeometry.ExtractNodeGeom(node, root);
            bool typeMatch = IsTypeMatch(node, item.uiType);
            breakdown.core = CalculateScoreFromGeometry(nodeGeom, psdGeom, typeMatch, config);
            return breakdown.total;
        }

        internal static PSDMatchScoring.ScoreBreakdown CalculateScoreFromGeometry(
            PSDMatchGeometry.NodeGeom nodeGeom,
            PSDMatchGeometry.PsdGeom psdGeom,
            bool isTypeMatch,
            PSDImportConfig config)
        {
            var input = PSDMatchScoring.ScoringInput.FromConfig(nodeGeom, psdGeom, isTypeMatch, config);
            return PSDMatchScoring.Evaluate(input);
        }

        private static float GetMatchScore(
            PicData item,
            RectTransform node,
            PSDMatchGeometry.PsdGeom psdGeom,
            PSDImportConfig config,
            bool useMlScore,
            PSDMatchModel mlModel,
            RectTransform rootTransform,
            PSDData cachedPsdData,
            out ScoreBreakdown breakdown)
        {
            if (!useMlScore || mlModel == null)
            {
                return CalculateMatchScoreDetailed(item, node, psdGeom, rootTransform, config, out breakdown);
            }

            float maxDist = mlModel.maxDistanceError > 0f ? mlModel.maxDistanceError : config.maxDistanceError;
            float maxSize = mlModel.maxSizeDiff > 0f ? mlModel.maxSizeDiff : config.maxSizeDiff;
            int maxDepth = mlModel.maxDepthDiff > 0 ? mlModel.maxDepthDiff : (config != null && config.mlConfig != null ? config.mlConfig.maxDepthDiff : 10);

            float[] x = PSDMatchFeatureExtractor.ExtractFeatures(item, node, rootTransform, cachedPsdData.width, cachedPsdData.height, maxDist, maxSize, maxDepth);
            float prob = PSDMatchML.Predict(mlModel, x);
            float score = prob * 100f;

            CalculateMatchScoreDetailed(item, node, psdGeom, rootTransform, config, out breakdown);
            breakdown.mlProb = prob;
            breakdown.mlUsed = true;
            return score;
        }

        private static bool IsTypeMatch(Transform node, string psdType)
        {
            if (psdType == "Button") return node.GetComponent<Button>() != null;
            if (psdType == "Text") return node.GetComponent<Text>() != null;
            if (psdType == "RawImage") return node.GetComponent<RawImage>() != null;
            if (psdType == "Image") return node.GetComponent<Image>() != null && node.GetComponent<Button>() == null;
            if (psdType == "Layout") return node.GetComponent<LayoutGroup>() != null;
            if (psdType == "Item") return node.GetComponent<LayoutGroup>() == null && node.GetComponentInParent<LayoutGroup>() != null;
            return false;
        }

        /// <summary>
        /// Phase 3.5: High-confidence exclusive-optimal pre-lock.
        /// Scans the score matrix for rows where the best candidate exceeds threshold AND
        /// no other row contests the same column (uniqueness check). Locked pairs are
        /// removed from subsequent Hungarian assignment.
        /// 
        /// Algorithm:
        /// 1. Row scan: find each row's best (max-score) valid candidate
        /// 2. Threshold filter: only keep rows where bestScore >= threshold
        /// 3. Column uniqueness: for each candidate, check if any other row scores
        ///    >= bestScore * columnRatio on the same column. If no contender → lockable.
        /// 4. Conflict resolution: if multiple rows lock same column, keep highest score.
        /// </summary>
        private static List<PreLockedPair> ApplyHighConfidencePreLock(
            List<BindingPairViewModel> pendingBinds,
            List<RectTransform> nodeList,
            float[,] scoreMatrix,
            bool[,] validMatrix,
            float threshold,
            float columnRatio,
            float perfectThreshold,
            HashSet<Transform> occupiedNodes,
            HashSet<BindingPairViewModel> matchedBindings,
            bool logDetail)
        {
            var lockedPairs = new List<PreLockedPair>();
            int rowCount = pendingBinds.Count;

            if (rowCount == 0 || nodeList.Count == 0)
                return lockedPairs;

            // --- Step 1: Row scan — find best candidate per row ---
            float[] rowBestScore = new float[rowCount];
            int[] rowBestCol = new int[rowCount];
            bool[] rowPassesThreshold = new bool[rowCount];

            for (int i = 0; i < rowCount; i++)
            {
                rowBestScore[i] = -1f;
                rowBestCol[i] = -1;
                for (int j = 0; j < nodeList.Count; j++)
                {
                    if (validMatrix[i, j] && scoreMatrix[i, j] > rowBestScore[i])
                    {
                        rowBestScore[i] = scoreMatrix[i, j];
                        rowBestCol[i] = j;
                    }
                }
                rowPassesThreshold[i] = rowBestCol[i] >= 0 && rowBestScore[i] >= threshold;
            }

            // --- Step 2 & 3: Column uniqueness + contention detection ---
            // For each row that passes threshold, check if any other row is a "contender"
            // on the same column (scores >= myBest * ratio).
            bool[] canLock = new bool[rowCount];
            for (int i = 0; i < rowCount; i++)
            {
                if (!rowPassesThreshold[i]) { canLock[i] = false; continue; }

                int col = rowBestCol[i];
                float myScore = rowBestScore[i];
                float contendThreshold = myScore * columnRatio;
                bool hasContender = false;

                for (int k = 0; k < rowCount; k++)
                {
                    if (k == i) continue;
                    if (validMatrix[k, col] && scoreMatrix[k, col] >= contendThreshold)
                    {
                        hasContender = true;
                        break;
                    }
                }

                canLock[i] = !hasContender;
            }

            // --- Step 4: Conflict resolution — if multiple rows target same column, keep highest ---
            // Track which columns are claimed and by which row (highest score wins)
            Dictionary<int, int> columnClaimed = new Dictionary<int, int>(); // col -> winning row index

            for (int i = 0; i < rowCount; i++)
            {
                if (!canLock[i]) continue;

                int col = rowBestCol[i];

                if (!columnClaimed.ContainsKey(col))
                {
                    columnClaimed[col] = i; // first claimant
                }
                else
                {
                    int existingRow = columnClaimed[col];
                    if (rowBestScore[i] > rowBestScore[existingRow])
                    {
                        // New row has higher score, displace previous claimant
                        canLock[existingRow] = false;
                        columnClaimed[col] = i;
                    }
                    else
                    {
                        // Existing claimant keeps it
                        canLock[i] = false;
                    }
                }
            }

            // --- Step 5: Execute locks ---
            for (int i = 0; i < rowCount; i++)
            {
                if (!canLock[i]) continue;

                int col = rowBestCol[i];
                float score = rowBestScore[i];
                var bind = pendingBinds[i];
                var node = nodeList[col];

                bool confirmed = score > perfectThreshold;
                string reason = string.Format("PreLock exclusive_optimal score={0:F0}", score);

                bind.unityNode = node.transform;
                bind.score = score;
                bind.isConfirmed = confirmed;
                bind.statusInfo = reason;
                bind.isIdMatched = false;
                matchedBindings.Add(bind);
                occupiedNodes.Add(node.transform);

                lockedPairs.Add(new PreLockedPair
                {
                    bindIndex = i,
                    nodeIndex = col,
                    score = score,
                    isConfirmed = confirmed,
                    reason = reason
                });

                if (logDetail)
                {
                    Debug.Log(string.Format(
                        "[Match] PreLock: {0} -> {1} score={2:F1} confirmed={3}",
                        bind.psdItem.pngName, GetTransformPath(node.transform), score, confirmed));
                }
            }

            if (logDetail && lockedPairs.Count > 0)
            {
                Debug.Log(string.Format(
                    "[Match] PreLock summary: {0}/{1} pairs locked, {2} sent to Hungarian",
                    lockedPairs.Count, rowCount, rowCount - lockedPairs.Count));
            }

            return lockedPairs;
        }

        private static string GetTransformPath(Transform t)
        {
            if (t == null)
            {
                return string.Empty;
            }

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
    }
}
