using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PSDImporter
{
    public static class VisualBindingRestoreService
    {
        private const float IdHistoryMinIou = 0.05f;
        private const float IdHistoryMaxCenterDistance = 80f;
        private const float IdHistoryMaxSizeErrorRatio = 0.75f;

        private struct ScoreBreakdown
        {
            public PSDMatchScoring.ScoreBreakdown core;

            public float distance => core.geometry.distance;
            public float diffW => core.geometry.diffW;
            public float diffH => core.geometry.diffH;
            public float scorePos => core.scorePos;
            public float scoreSize => core.scoreSize;
            public float scoreType => core.scoreType;
            public float scoreDepth => core.scoreDepth;
            public float scoreAnchor => core.scoreAnchor;
            public float weightedPos => core.weightedPos;
            public float weightedSize => core.weightedSize;
            public float weightedType => core.weightedType;
            public float weightedDepth => core.weightedDepth;
            public float weightedAnchor => core.weightedAnchor;
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
                PSDMatchGeometry.RebuildLayoutForGeometry(rootRectTransform);
                bool logDetail = matchConfig.showDetailedLog;
                bool forceCandidateLog = matchConfig.forceCandidateLog;
                bool logCandidatesInLoop = logDetail && !forceCandidateLog;
                bool stdPrefabEnabled = PSDStdPrefabSupport.HasAnyStdPrefabMatchEnabled(matchConfig);
                bool idHistoryEnabled = matchConfig.enableIdHistoryMatch;

                float perfectThreshold = 150f;
                HashSet<Transform> occupiedNodes = new HashSet<Transform>();
                HashSet<BindingPairViewModel> matchedBindings = new HashSet<BindingPairViewModel>();

                if (logDetail)
                {
                    Debug.Log($"[Match] Config maxDist={matchConfig.maxDistanceError:F1}, maxSizeDiff={matchConfig.maxSizeDiff:F1}, weightPos={matchConfig.weightPosition:F2}, weightSize={matchConfig.weightSize:F2}, weightType={matchConfig.weightType:F2}, weightDepth={matchConfig.weightDepth:F2}, weightAnchor={matchConfig.weightAnchor:F2}, maxDepthDiff={matchConfig.maxDepthDiff}, idHistory={idHistoryEnabled}, skipInactive={matchConfig.skipInactiveMatch}, allowUnmatched={matchConfig.allowUnmatched}, unmatchedPenalty={matchConfig.unmatchedPenalty:F1}, minAcceptScore={matchConfig.minAcceptScore:F1}, forceCandidateLog={forceCandidateLog}");
                }

                foreach (var bind in bindings)
                {
                    ResetMatchDiagnostics(bind);
                    if (bind.isConfirmed && bind.unityNode != null)
                    {
                        bool canKeepConfirmed = true;
                        if (stdPrefabEnabled && PSDStdPrefabSupport.IsStdPrefabRoot(bind.psdItem))
                        {
                            canKeepConfirmed = PSDStdPrefabSupport.IsReservedStdPrefabNode(bind.unityNode as RectTransform, matchConfig);
                            bind.stdPrefabMode = canKeepConfirmed ? StdPrefabApplyMode.ReuseExisting : StdPrefabApplyMode.None;
                        }

                        if (canKeepConfirmed)
                        {
                            SetFixedMatchConfidence(bind, bind.score > 0f ? bind.score : 9999f);
                            occupiedNodes.Add(bind.unityNode);
                            matchedBindings.Add(bind);
                        }
                        else
                        {
                            bind.unityNode = null;
                            bind.score = 0;
                            bind.isConfirmed = false;
                            bind.statusInfo = "Waiting for match";
                            bind.isIdMatched = false;
                            ResetMatchDiagnostics(bind);
                            bind.stdPrefabAssetPath = null;
                        }
                    }
                    else
                    {
                        bind.unityNode = null;
                        bind.score = 0;
                        bind.statusInfo = "Waiting for match";
                        bind.isIdMatched = false;
                        ResetMatchDiagnostics(bind);
                        bind.stdPrefabMode = StdPrefabApplyMode.None;
                        bind.stdPrefabAssetPath = null;
                    }
                }

                List<BindingPairViewModel> stdPrefabBinds = new List<BindingPairViewModel>();
                List<BindingPairViewModel> pendingBinds = new List<BindingPairViewModel>();
                foreach (var bind in bindings)
                {
                    if (matchedBindings.Contains(bind))
                    {
                        continue;
                    }

                    if (idHistoryEnabled && bindingAsset != null)
                    {
                        GameObject savedGo = bindingAsset.GetBindTarget(bind.psdItem.id, rootTransform);
                        bool canUseSavedGo = savedGo != null &&
                                             savedGo.transform.IsChildOf(rootTransform) &&
                                             !occupiedNodes.Contains(savedGo.transform);

                        if (canUseSavedGo && stdPrefabEnabled && PSDStdPrefabSupport.IsStdPrefabRoot(bind.psdItem))
                        {
                            canUseSavedGo = PSDStdPrefabSupport.IsReservedStdPrefabNode(savedGo.transform as RectTransform, matchConfig);
                        }

                        if (canUseSavedGo)
                        {
                            if (!IsIdHistoryGeometryAcceptable(bind, savedGo.transform as RectTransform, rootRectTransform, cachedPsdData, out string rejectReason))
                            {
                                bind.idHistoryRejected = true;
                                bind.idHistoryRejectedPath = GetTransformPath(savedGo.transform);
                                bind.idHistoryRejectReason = rejectReason;
                                bind.statusInfo = $"ID history rejected: {rejectReason}";
                                if (logDetail)
                                {
                                    Debug.Log($"[Match] Reject saved binding for {bind.psdItem.pngName} -> {GetTransformPath(savedGo.transform)} ({rejectReason})");
                                }
                            }
                            else
                            {
                                bind.unityNode = savedGo.transform;
                                bind.score = 9999f;
                                bind.statusInfo = "ID history binding";
                                bind.isIdMatched = true;
                                bind.isConfirmed = true;
                                SetFixedMatchConfidence(bind, bind.score);
                                if (stdPrefabEnabled && PSDStdPrefabSupport.IsStdPrefabRoot(bind.psdItem))
                                {
                                    bind.stdPrefabMode = StdPrefabApplyMode.ReuseExisting;
                                }
                                matchedBindings.Add(bind);
                                occupiedNodes.Add(savedGo.transform);

                                if (logDetail)
                                {
                                    Debug.Log($"[Match] Saved binding for {bind.psdItem.pngName} -> {GetTransformPath(savedGo.transform)} score=9999 (ID)");
                                }
                                continue;
                            }
                        }
                    }

                    if (stdPrefabEnabled && PSDStdPrefabSupport.IsStdPrefabRoot(bind.psdItem))
                    {
                        stdPrefabBinds.Add(bind);
                        bind.statusInfo = "Waiting for standard prefab match";
                        continue;
                    }

                    pendingBinds.Add(bind);
                }

                if (stdPrefabEnabled && stdPrefabBinds.Count > 0)
                {
                    List<BindingPairViewModel> earlyStdPrefabBinds = stdPrefabBinds
                        .Where(b => b != null && (PSDStdPrefabSupport.IsStdButton(b.psdItem) || PSDStdPrefabSupport.IsStdPopup(b.psdItem)))
                        .ToList();
                    if (earlyStdPrefabBinds.Count > 0)
                    {
                        ResolveStdPrefabBindings(earlyStdPrefabBinds, targetRoot, cachedPsdData, matchConfig, occupiedNodes, matchedBindings, logDetail);
                        stdPrefabBinds.RemoveAll(b => b != null && (PSDStdPrefabSupport.IsStdButton(b.psdItem) || PSDStdPrefabSupport.IsStdPopup(b.psdItem)));
                    }
                }

                var allNodes = targetRoot.GetComponentsInChildren<RectTransform>(true);
                List<RectTransform> nodeList = new List<RectTransform>();
                List<RectTransform> nodeListForLog = forceCandidateLog ? new List<RectTransform>() : nodeList;

                int skippedRoot = 0;
                int skippedOccupied = 0;
                int skippedInactive = 0;
                int skippedStdPrefab = 0;
                int skippedRootLog = 0;
                int skippedInactiveLog = 0;
                int skippedOccupiedLog = 0;
                int skippedStdPrefabLog = 0;

                foreach (var node in allNodes)
                {
                    if (node == rootTransform)
                    {
                        skippedRoot++;
                        skippedRootLog++;
                        continue;
                    }

                    if (PSDMatchNodeFilter.ShouldSkipInactiveMatch(node, rootTransform, matchConfig))
                    {
                        skippedInactive++;
                        skippedInactiveLog++;
                        continue;
                    }

                    if (PSDStdPrefabSupport.IsInsideReservedStdPrefab(node, matchConfig))
                    {
                        skippedStdPrefab++;
                        skippedStdPrefabLog++;
                        continue;
                    }

                    if (IsInsideMatchedStdPrefab(node, matchedBindings))
                    {
                        skippedStdPrefab++;
                        skippedStdPrefabLog++;
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
                    Debug.Log($"[Match] Candidate nodes={nodeList.Count}, skipped(root:{skippedRoot}, occupied:{skippedOccupied}, inactive:{skippedInactive}, std:{skippedStdPrefab})");
                    if (forceCandidateLog)
                    {
                        Debug.Log($"[Match] Candidate nodes(for log)={nodeListForLog.Count}, skipped(root:{skippedRootLog}, occupied:{skippedOccupiedLog}, inactive:{skippedInactiveLog}, std:{skippedStdPrefabLog})");
                    }
                }

                if (logDetail && forceCandidateLog)
                {
                    foreach (var bind in bindings)
                    {
                        LogTopCandidatesForBind(bind, nodeListForLog, rootTransform, cachedPsdData, matchConfig, skippedRootLog, skippedInactiveLog, skippedOccupiedLog);
                    }
                }

                int itemCount = pendingBinds.Count;
                int nodeCount = nodeList.Count;
                Dictionary<RectTransform, PSDMatchGeometry.NodeGeom> nodeGeomCache = BuildNodeGeomCache(nodeList, rootRectTransform);
                if (itemCount == 0 || nodeCount == 0)
                {
                    foreach (var bind in pendingBinds)
                    {
                        bind.statusInfo = "No suitable node found";
                    }
                    ResolveStdPrefabBindings(stdPrefabBinds, targetRoot, cachedPsdData, matchConfig, occupiedNodes, matchedBindings, logDetail);
                    LogUnmatchedSummary(bindings);
                    if (logDetail)
                    {
                        PSDMatchLogExporter.Export(bindings, targetRoot, cachedPsdData, matchConfig);
                    }
                    return;
                }

                // ── P2: 构建 PSD parentId → 已匹配白膜 Transform 映射 ──────────────────
                // 每次计算前快照当前 matchedBindings（ID历史绑定）
                // 在评分矩阵阶段动态查询（串行 i 循环，前序已匹配项可被后续使用）
                // key: PSD id, value: 已绑定的白膜 Transform
                Dictionary<int, Transform> psdIdToMatchedNode = new Dictionary<int, Transform>();
                foreach (var mb in matchedBindings)
                {
                    if (mb.unityNode != null)
                        RegisterHierarchyNode(psdIdToMatchedNode, mb.psdItem, mb.unityNode, cachedPsdData);
                }

                float[,] scoreMatrix = new float[itemCount, nodeCount];
                bool[,] validMatrix = new bool[itemCount, nodeCount];
                float maxScore = 0f;
                int totalSkippedSpatial = 0;
                int totalHierarchyPenalized = 0;

                for (int i = 0; i < itemCount; i++)
                {
                    var bind = pendingBinds[i];
                    ResetMatchDiagnostics(bind, clearHistoryDiagnostics: false);
                    var psdGeom = PSDMatchGeometry.BuildPsdGeom(bind.psdItem, cachedPsdData.width, cachedPsdData.height);
                    List<MatchCandidate> localCandidates = logCandidatesInLoop ? new List<MatchCandidate>() : null;
                    int skippedType = 0;
                    int skippedSpatial = 0;
                    int hierarchyPenalized = 0;

                    // ── P2: 查询此 PSD 项的父节点是否已匹配到白膜节点 ──────────────────
                    Transform matchedParentNode = null;
                    bool useHierarchyPreference = matchConfig.parentAffinityBonus > 1f ||
                                                  matchConfig.hierarchyDescendantAffinityBonus > 1f ||
                                                  matchConfig.hierarchyOutsideParentPenalty < 1f;
                    if (bind.psdItem.parentNodeId > 0 && useHierarchyPreference)
                    {
                        psdIdToMatchedNode.TryGetValue(bind.psdItem.parentNodeId, out matchedParentNode);
                    }

                    for (int j = 0; j < nodeCount; j++)
                    {
                        var node = nodeList[j];

                        // ── P0: 类型兼容性评分 ──────────────────────────────────────────
                        float typeScore = GetTypeMatchScore(node, bind.psdItem, matchConfig.typeCompatScore);
                        if (typeScore <= 0f)
                        {
                            skippedType++;
                            continue;
                        }

                        PSDMatchGeometry.NodeGeom nodeGeom = GetCachedNodeGeom(node, rootRectTransform, nodeGeomCache);
                        float distance = Vector2.Distance(nodeGeom.centerLocal, psdGeom.centerLocal);
                        if (ShouldPruneByDistance(distance, matchConfig))
                        {
                            skippedSpatial++;
                            continue;
                        }

                        // ── P2: 父级亲和力乘数 ────────────────────────────────────────
                        // 如果此节点的父节点 == 已匹配的白膜父节点，乘以 parentAffinityBonus
                        float parentAffinity = 1f;
                        float hierarchyTotalMultiplier = 1f;
                        if (matchedParentNode != null)
                        {
                            if (node.parent == matchedParentNode)
                            {
                                parentAffinity = matchConfig.parentAffinityBonus;
                            }
                            else if (node.IsChildOf(matchedParentNode))
                            {
                                parentAffinity = matchConfig.hierarchyDescendantAffinityBonus;
                            }
                            else
                            {
                                hierarchyTotalMultiplier = Mathf.Clamp01(matchConfig.hierarchyOutsideParentPenalty);
                                hierarchyPenalized++;
                            }
                        }

                        ScoreBreakdown breakdown = default;
                        breakdown.core = CalculateScoreFromGeometry(nodeGeom, psdGeom, typeScore, matchConfig, parentAffinity, bind.psdItem, node);
                        float score = ApplyHierarchyTotalMultiplier(ref breakdown, hierarchyTotalMultiplier);
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
                        sb.AppendLine($"[Match] Item {bind.psdItem.pngName} (id:{bind.psdItem.id}, type:{bind.psdItem.uiType}) centerLocal=({psdGeom.centerLocal.x:F1},{psdGeom.centerLocal.y:F1}) size=({psdGeom.sizeLocal.x:F1},{psdGeom.sizeLocal.y:F1}) candidates={localCandidates.Count} skipped(type:{skippedType}, spatial:{skippedSpatial}, inactive:{skippedInactive}, occupied:{skippedOccupied}, root:{skippedRoot}) hierarchyPenalty={hierarchyPenalized}");
                        var top = localCandidates.OrderByDescending(c => c.score).Take(5).ToList();
                        for (int k = 0; k < top.Count; k++)
                        {
                            var cand = top[k];
                            var b = cand.breakdown;
                            var nodeGeom = GetCachedNodeGeom(cand.node, rootRectTransform, nodeGeomCache);
                            string geomInfo = matchConfig.logMatchGeometry ? $" nodeCenterLocal=({nodeGeom.centerLocal.x:F1},{nodeGeom.centerLocal.y:F1}) nodeSizeLocal=({nodeGeom.sizeLocal.x:F1},{nodeGeom.sizeLocal.y:F1}) psdCenterLocal=({psdGeom.centerLocal.x:F1},{psdGeom.centerLocal.y:F1}) psdSizeLocal=({psdGeom.sizeLocal.x:F1},{psdGeom.sizeLocal.y:F1})" : string.Empty;
                            string affinityInfo = string.Empty;
                            if (matchedParentNode != null)
                            {
                                if (cand.node.parent == matchedParentNode)
                                {
                                    affinityInfo = $" [parent x{matchConfig.parentAffinityBonus:F1}]";
                                }
                                else if (cand.node.IsChildOf(matchedParentNode))
                                {
                                    affinityInfo = $" [descendant x{matchConfig.hierarchyDescendantAffinityBonus:F1}]";
                                }
                                else
                                {
                                    affinityInfo = $" [outside x{matchConfig.hierarchyOutsideParentPenalty:F2}]";
                                }
                            }
                            sb.AppendLine($"  #{k + 1} {GetTransformPath(cand.node)} {PSDMatchNodeFilter.FormatActiveState(cand.node.gameObject)}{geomInfo} dist={b.distance:F1} diff=({b.diffW:F1},{b.diffH:F1}) scorePos={b.scorePos:F1} scoreSize={b.scoreSize:F1} scoreType={b.scoreType:F0} scoreDepth={b.scoreDepth:F0} scoreAnchor={b.scoreAnchor:F0} w=({b.weightedPos:F1},{b.weightedSize:F1},{b.weightedType:F1},{b.weightedDepth:F1},{b.weightedAnchor:F1}) total={b.total:F1}{affinityInfo}");
                        }
                        Debug.Log(sb.ToString());
                    }

                    bind.skippedTypeCandidates = skippedType;
                    bind.skippedSpatialCandidates = skippedSpatial;
                    bind.hierarchyPenalizedCandidates = hierarchyPenalized;
                    totalSkippedSpatial += skippedSpatial;
                    totalHierarchyPenalized += hierarchyPenalized;
                }

                if (logDetail)
                {
                    Debug.Log($"[Match] Candidate pruning summary: skippedSpatial={totalSkippedSpatial}, hierarchyPenalized={totalHierarchyPenalized}");
                }


                // ========================================================================
                // Phase 3.5: High-Confidence Pre-Lock (Layered Matching)
                // Lock exclusive-optimal match pairs BEFORE Hungarian to prevent
                // global-optimal misassignment where obvious pairs get split apart.
                // ========================================================================
                if (matchConfig.preLockEnabled && pendingBinds.Count > 0 && nodeList.Count > 0)
                {
                    float effectivePreLockThreshold = GetEffectivePreLockThreshold(matchConfig, maxScore);
                    if (logDetail)
                    {
                        Debug.Log($"[Match] PreLock threshold effective={effectivePreLockThreshold:F1} (configured={matchConfig.preLockThreshold:F1}, maxScore={maxScore:F1}, dynamic={matchConfig.preLockUseDynamicThreshold})");
                    }

                    var preLocked = ApplyHighConfidencePreLock(
                        pendingBinds,
                        nodeList,
                        scoreMatrix,
                        validMatrix,
                        effectivePreLockThreshold,
                        matchConfig.preLockColumnUniquenessRatio,
                        matchConfig.preLockMinScoreGap,
                        perfectThreshold,
                        matchConfig,
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
                        nodeGeomCache = BuildNodeGeomCache(remainingNodes, rootRectTransform);

                        // If everything was locked, skip Hungarian entirely
                        if (remainingBinds.Count == 0 || remainingNodes.Count == 0)
                        {
                            if (logDetail)
                            {
                                Debug.Log($"[Match] All {pendingBinds.Count} pairs resolved by pre-lock, skipping Hungarian.");
                            }
                            // Early exit — all bindings are already set
                            ResolveStdPrefabBindings(stdPrefabBinds, targetRoot, cachedPsdData, matchConfig, occupiedNodes, matchedBindings, logDetail);
                            LogUnmatchedSummary(bindings);
                            if (logDetail)
                            {
                                PSDMatchLogExporter.Export(bindings, targetRoot, cachedPsdData, matchConfig);
                            }
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
                    ResolveStdPrefabBindings(stdPrefabBinds, targetRoot, cachedPsdData, matchConfig, occupiedNodes, matchedBindings, logDetail);
                    LogUnmatchedSummary(bindings);
                    if (logDetail)
                    {
                        PSDMatchLogExporter.Export(bindings, targetRoot, cachedPsdData, matchConfig);
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
                        SetUnmatchedMatchConfidence(bind, i, scoreMatrix, validMatrix);
                        bind.statusInfo = "Unmatched: invalid assignment";
                        continue;
                    }

                    if (isDummyAssignment)
                    {
                        bind.unityNode = null;
                        bind.score = 0f;
                        bind.isConfirmed = false;
                        SetUnmatchedMatchConfidence(bind, i, scoreMatrix, validMatrix);
                        bind.statusInfo = "Unmatched: assigned to dummy";
                        continue;
                    }

                    if (!validMatrix[i, j])
                    {
                        bind.unityNode = null;
                        bind.score = 0f;
                        bind.isConfirmed = false;
                        SetUnmatchedMatchConfidence(bind, i, scoreMatrix, validMatrix);
                        bind.statusInfo = "Unmatched: invalid candidate";
                        continue;
                    }

                    var node = nodeList[j];
                    if (occupiedNodes.Contains(node.transform))
                    {
                        bind.unityNode = null;
                        bind.score = 0f;
                        bind.isConfirmed = false;
                        SetUnmatchedMatchConfidence(bind, i, scoreMatrix, validMatrix);
                        bind.statusInfo = "Unmatched: node already occupied";
                        continue;
                    }

                    bind.unityNode = node;
                    bind.score = scoreMatrix[i, j];
                    bind.isIdMatched = false;
                    SetMatchConfidence(bind, bind.score, i, j, scoreMatrix, validMatrix, matchConfig);

                    if (matchConfig.allowUnmatched && bind.score < matchConfig.minAcceptScore)
                    {
                        bind.unityNode = null;
                        bind.isConfirmed = false;
                        SetUnmatchedMatchConfidence(bind, i, scoreMatrix, validMatrix);
                        bind.statusInfo = $"Unmatched: score {bind.score:F1} < minAcceptScore {matchConfig.minAcceptScore:F1}";
                        continue;
                    }

                    bind.statusInfo = bind.isLowConfidence
                        ? $"Low confidence: score {bind.score:F0}, margin {bind.scoreMargin:F1}"
                        : $"Score: {bind.score:F0}, margin {bind.scoreMargin:F1}";
                    if (bind.score > perfectThreshold && !bind.isLowConfidence)
                    {
                        bind.isConfirmed = true;
                    }

                    matchedBindings.Add(bind);
                    occupiedNodes.Add(node.transform);

                    if (logDetail)
                    {
                        ScoreBreakdown breakdown;
                        var psdGeom = PSDMatchGeometry.BuildPsdGeom(bind.psdItem, cachedPsdData.width, cachedPsdData.height);
                        float typeScore = GetTypeMatchScore(node, bind.psdItem, matchConfig.typeCompatScore);
                        GetMatchScore(bind.psdItem, node, psdGeom, matchConfig, rootRectTransform, out breakdown, typeScore);
                        var nodeGeom = GetCachedNodeGeom(node, rootRectTransform, nodeGeomCache);
                        string geomInfo = matchConfig.logMatchGeometry ? $" nodeCenterLocal=({nodeGeom.centerLocal.x:F1},{nodeGeom.centerLocal.y:F1}) nodeSizeLocal=({nodeGeom.sizeLocal.x:F1},{nodeGeom.sizeLocal.y:F1}) psdCenterLocal=({psdGeom.centerLocal.x:F1},{psdGeom.centerLocal.y:F1}) psdSizeLocal=({psdGeom.sizeLocal.x:F1},{psdGeom.sizeLocal.y:F1})" : string.Empty;
                        Debug.Log($"[Match] Result {bind.psdItem.pngName} -> {GetTransformPath(node)} score={bind.score:F1} margin={bind.scoreMargin:F1} lowConfidence={bind.isLowConfidence} dist={breakdown.distance:F1} diff=({breakdown.diffW:F1},{breakdown.diffH:F1}){geomInfo} w=({breakdown.weightedPos:F1},{breakdown.weightedSize:F1},{breakdown.weightedType:F1})");
                    }
                }

                foreach (var bind in pendingBinds)
                {
                    if (!matchedBindings.Contains(bind) && bind.unityNode == null && string.IsNullOrEmpty(bind.statusInfo))
                    {
                        bind.statusInfo = "No suitable node found";
                    }
                }

                ResolveStdPrefabBindings(stdPrefabBinds, targetRoot, cachedPsdData, matchConfig, occupiedNodes, matchedBindings, logDetail);
                LogUnmatchedSummary(bindings);

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
            string psdPath,
            bool storeBindingObjectReferences = true,
            bool saveAssets = true)
        {
            if (targetRoot == null || cachedPsdData == null || bindings == null)
            {
                return 0;
            }

            Undo.RegisterFullObjectHierarchyUndo(targetRoot, "Apply Visual Bindings");
            PSDImageReuseLogStore.Reset();

            // ── P1: 构建 PSD id → 已匹配白膜 Transform 的映射（用于自动创建时查父节点） ──
            bool autoCreate = config == null || config.autoCreateUnmatched;
            Dictionary<int, Transform> psdIdToNode = new Dictionary<int, Transform>();
            foreach (var b in bindings)
            {
                if (b.unityNode != null)
                    RegisterHierarchyNode(psdIdToNode, b.psdItem, b.unityNode, cachedPsdData);
            }

            int count = 0;
            int autoCreatedCount = 0;

            for (int i = 0; i < bindings.Count; i++)
            {
                var bind = bindings[i];

                bool treatAsStdReuse = bind.unityNode != null &&
                                       (bind.stdPrefabMode == StdPrefabApplyMode.ReuseExisting ||
                                        (bind.stdPrefabMode == StdPrefabApplyMode.None &&
                                         config != null &&
                                         PSDStdPrefabSupport.HasAnyStdPrefabMatchEnabled(config) &&
                                         PSDStdPrefabSupport.IsStdPrefabRoot(bind.psdItem) &&
                                         PSDStdPrefabSupport.IsReservedStdPrefabNode(bind.unityNode as RectTransform, config)));

                if (treatAsStdReuse)
                {
                    bind.stdPrefabMode = StdPrefabApplyMode.ReuseExisting;
                    PSDCreateor.RefreshStdPrefabRoot(bind.unityNode.gameObject, bind.psdItem, cachedPsdData, config);
                    SaveBindingIfNeeded(bindingAsset, bind, targetRoot.transform, storeBindingObjectReferences);
                    count++;
                    RegisterHierarchyNode(psdIdToNode, bind.psdItem, bind.unityNode, cachedPsdData);
                    continue;
                }

                if (bind.stdPrefabMode == StdPrefabApplyMode.InstantiatePending)
                {
                    Transform stdParent = ResolveParentTransform(targetRoot.transform, bind.psdItem, cachedPsdData, psdIdToNode);
                    GameObject stdPrefabGo = PSDStdPrefabSupport.InstantiatePendingPrefab(bind, stdParent);
                    if (stdPrefabGo != null)
                    {
                        Undo.RegisterCreatedObjectUndo(stdPrefabGo, "Instantiate Standard Prefab");
                        PSDCreateor.RefreshStdPrefabRoot(stdPrefabGo, bind.psdItem, cachedPsdData, config);
                        bind.unityNode = stdPrefabGo.transform;
                        bind.isAutoCreated = true;
                        bind.isConfirmed = true;
                        bind.statusInfo = $"Std prefab instantiated under '{stdParent.name}'";
                        SaveBindingIfNeeded(bindingAsset, bind, targetRoot.transform, storeBindingObjectReferences);
                        RegisterHierarchyNode(psdIdToNode, bind.psdItem, bind.unityNode, cachedPsdData);
                        autoCreatedCount++;
                        count++;
                    }
                    else
                    {
                        bind.statusInfo = $"Std prefab missing: {bind.stdPrefabAssetPath}";
                    }
                    continue;
                }

                // ── 正常匹配路径 ─────────────────────────────────────────────────────
                if (bind.unityNode != null)
                {
                    PSDCreateor.RefreshNode(bind.unityNode.gameObject, bind.psdItem, cachedPsdData, true, config);
                    SaveBindingIfNeeded(bindingAsset, bind, targetRoot.transform, storeBindingObjectReferences);
                    count++;
                    // 更新映射（RefreshNode 可能未改变 Transform，但确保最新）
                    RegisterHierarchyNode(psdIdToNode, bind.psdItem, bind.unityNode, cachedPsdData);
                    continue;
                }

                // ── P1: Unmatched → 自动创建节点 ──────────────────────────────────
                if (!autoCreate) continue;

                if (PSDScrollRectUtility.IsScrollRectRoot(bind.psdItem))
                {
                    bind.statusInfo = "Unmatched ScrollRect root: restore mode does not auto-create scroll structures";
                    continue;
                }

                if (PSDScrollRectUtility.IsInsideScrollRect(bind.psdItem, cachedPsdData) &&
                    PSDScrollRectUtility.ResolveParentForItem(bind.psdItem, cachedPsdData, psdIdToNode) == null)
                {
                    bind.statusInfo = "Skipped auto-create: ScrollRect root/content is not matched";
                    continue;
                }

                // 找父节点
                Transform parentTransform = ResolveParentTransform(targetRoot.transform, bind.psdItem, cachedPsdData, psdIdToNode);

                // 创建新 GameObject
                string nodeName = !string.IsNullOrEmpty(bind.psdItem.cleanName)
                    ? bind.psdItem.cleanName
                    : (!string.IsNullOrEmpty(bind.psdItem.pngName) ? bind.psdItem.pngName : "AutoNode");
                GameObject newGo = new GameObject(nodeName);
                newGo.transform.SetParent(parentTransform, false);
                Undo.RegisterCreatedObjectUndo(newGo, "Auto Create Unmatched Node");

                // 确保有 RectTransform
                if (newGo.GetComponent<RectTransform>() == null)
                {
                    newGo.AddComponent<RectTransform>();
                }

                // 初始化节点内容
                PSDCreateor.RefreshNode(newGo, bind.psdItem, cachedPsdData, true, config);

                // 回写到 bind
                bind.unityNode = newGo.transform;
                bind.isAutoCreated = true;
                bind.isConfirmed = false; // 自动创建不计为已确认绑定（避免写入持久化）
                bind.statusInfo = $"Auto-created under '{parentTransform.name}'";

                // 更新父节点映射，以便同级别后续节点能找到当前新建节点作父
                RegisterHierarchyNode(psdIdToNode, bind.psdItem, newGo.transform, cachedPsdData);

                autoCreatedCount++;
                count++;
            }

            for (int i = 0; i < bindings.Count; i++)
            {
                var bind = bindings[i];
                if (bind == null || bind.unityNode == null)
                {
                    continue;
                }

                PSDCreateor.ApplyPsdLayoutChildOrder(bind.unityNode, bind.psdItem, cachedPsdData);
            }

            if (autoCreatedCount > 0)
            {
                Debug.Log($"[ApplyBindings] Auto-created {autoCreatedCount} new nodes for unmatched PSD items.");
            }

            if (bindingAsset != null)
            {
                EditorUtility.SetDirty(bindingAsset);
            }
            if (saveAssets)
            {
                AssetDatabase.SaveAssets();
            }

            if (config == null || config.showDetailedLog)
            {
                Debug.Log("[PSD ImageReuse] " + PSDImageReuseLogStore.BuildSummary());
            }

            return count;
        }

        private static void ResolveStdPrefabBindings(
            List<BindingPairViewModel> stdPrefabBinds,
            GameObject targetRoot,
            PSDData cachedPsdData,
            PSDImportConfig config,
            HashSet<Transform> occupiedNodes,
            HashSet<BindingPairViewModel> matchedBindings,
            bool logDetail)
        {
            if (stdPrefabBinds == null || stdPrefabBinds.Count == 0)
            {
                return;
            }

            Dictionary<int, Transform> psdIdToMatchedNode = new Dictionary<int, Transform>();
            foreach (BindingPairViewModel binding in matchedBindings)
            {
                if (binding != null && binding.unityNode != null)
                {
                    RegisterHierarchyNode(psdIdToMatchedNode, binding.psdItem, binding.unityNode, cachedPsdData);
                }
            }

            PSDStdPrefabSupport.ResolveStdPrefabBindings(
                stdPrefabBinds,
                targetRoot,
                cachedPsdData,
                config,
                occupiedNodes,
                matchedBindings,
                psdIdToMatchedNode,
                logDetail);
        }

        private static void LogUnmatchedSummary(List<BindingPairViewModel> bindings)
        {
            int unmatchedCount = bindings.Count(b => b.unityNode == null && b.stdPrefabMode != StdPrefabApplyMode.InstantiatePending);
            int pendingInstantiateCount = bindings.Count(b => b.stdPrefabMode == StdPrefabApplyMode.InstantiatePending);
            float unmatchedRate = bindings.Count > 0 ? (float)unmatchedCount / bindings.Count : 0f;
            Debug.Log($"[Match] Unmatched rate: {unmatchedCount}/{bindings.Count} ({unmatchedRate:P1}), stdInstantiatePending={pendingInstantiateCount}");
        }

        private static void SaveBindingIfNeeded(PSDBindingData bindingAsset, BindingPairViewModel bind, Transform root, bool storeObjectReference)
        {
            if (bindingAsset == null || bind == null || bind.unityNode == null)
            {
                return;
            }

            if (bind.isConfirmed || bind.stdPrefabMode != StdPrefabApplyMode.None)
            {
                bindingAsset.SaveBinding(bind.psdItem.id, bind.psdItem.pngName, bind.unityNode.gameObject, root, storeObjectReference);
            }
        }

        private static Transform ResolveParentTransform(Transform defaultParent, int parentNodeId, Dictionary<int, Transform> psdIdToNode)
        {
            if (parentNodeId > 0 && psdIdToNode != null && psdIdToNode.TryGetValue(parentNodeId, out Transform found) && found != null)
            {
                return found;
            }

            return defaultParent;
        }

        private static Transform ResolveParentTransform(Transform defaultParent, PicData item, PSDData psdData, Dictionary<int, Transform> psdIdToNode)
        {
            Transform scrollParent = PSDScrollRectUtility.ResolveParentForItem(item, psdData, psdIdToNode);
            if (scrollParent != null)
            {
                return scrollParent;
            }

            return ResolveParentTransform(defaultParent, item.parentNodeId, psdIdToNode);
        }

        private static void RegisterHierarchyNode(Dictionary<int, Transform> psdIdToNode, PicData item, Transform node, PSDData psdData)
        {
            PSDScrollRectUtility.RegisterHierarchyNode(item, node, psdData, psdIdToNode);
        }

        private static void LogTopCandidatesForBind(
            BindingPairViewModel bind,
            IList<RectTransform> nodes,
            Transform rootTransform,
            PSDData cachedPsdData,
            PSDImportConfig config,
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
                float typeScore = GetTypeMatchScore(node, bind.psdItem, config.typeCompatScore);
                if (typeScore <= 0f)
                {
                    skippedType++;
                    continue;
                }

                ScoreBreakdown breakdown;
                float score = GetMatchScore(bind.psdItem, node, psdGeom, config, rootRect, out breakdown, typeScore);
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
                string geomInfo = config.logMatchGeometry ? $" nodeCenterLocal=({nodeGeom.centerLocal.x:F1},{nodeGeom.centerLocal.y:F1}) nodeSizeLocal=({nodeGeom.sizeLocal.x:F1},{nodeGeom.sizeLocal.y:F1}) psdCenterLocal=({psdGeom.centerLocal.x:F1},{psdGeom.centerLocal.y:F1}) psdSizeLocal=({psdGeom.sizeLocal.x:F1},{psdGeom.sizeLocal.y:F1})" : string.Empty;
                sb.AppendLine($"  #{k + 1} {GetTransformPath(cand.node)} {PSDMatchNodeFilter.FormatActiveState(cand.node.gameObject)}{geomInfo} dist={b.distance:F1} diff=({b.diffW:F1},{b.diffH:F1}) scorePos={b.scorePos:F1} scoreSize={b.scoreSize:F1} scoreType={b.scoreType:F0} scoreDepth={b.scoreDepth:F0} scoreAnchor={b.scoreAnchor:F0} w=({b.weightedPos:F1},{b.weightedSize:F1},{b.weightedType:F1},{b.weightedDepth:F1},{b.weightedAnchor:F1}) total={b.total:F1}");
            }
            Debug.Log(sb.ToString());
        }

        private static float CalculateMatchScoreDetailed(
            PicData item,
            RectTransform node,
            PSDMatchGeometry.PsdGeom psdGeom,
            RectTransform root,
            PSDImportConfig config,
            out ScoreBreakdown breakdown,
            float parentAffinity = 1f,
            float typeScoreOverride = -1f)
        {
            breakdown = new ScoreBreakdown();
            // PicData is a struct, use default comparison for value type null check
            if (config == null || item.Equals(default) || node == null || root == null)
            {
                return 0f;
            }

            var nodeGeom = PSDMatchGeometry.ExtractNodeGeom(node, root);
            float typeScore = typeScoreOverride >= 0f
                ? typeScoreOverride
                : GetTypeMatchScore(node, item, config.typeCompatScore);
            breakdown.core = CalculateScoreFromGeometry(nodeGeom, psdGeom, typeScore, config, parentAffinity, item, node);
            return breakdown.total;
        }

        internal static PSDMatchScoring.ScoreBreakdown CalculateScoreFromGeometry(
            PSDMatchGeometry.NodeGeom nodeGeom,
            PSDMatchGeometry.PsdGeom psdGeom,
            float typeMatchScore,
            PSDImportConfig config,
            float parentAffinity = 1f,
            PicData item = default,
            RectTransform node = null)
        {
            var input = PSDMatchScoring.ScoringInput.FromConfigForItem(nodeGeom, psdGeom, typeMatchScore, config, item, node, parentAffinity);
            return PSDMatchScoring.Evaluate(input);
        }

        private static float GetMatchScore(
            PicData item,
            RectTransform node,
            PSDMatchGeometry.PsdGeom psdGeom,
            PSDImportConfig config,
            RectTransform rootTransform,
            out ScoreBreakdown breakdown,
            float typeScore = 100f,
            float parentAffinity = 1f)
        {
            return CalculateMatchScoreDetailed(item, node, psdGeom, rootTransform, config, out breakdown, parentAffinity, typeScore);
        }

        /// <summary>
        /// 类型匹配分：100=完全匹配，0&lt;=compatScore=兼容匹配（如Text白膜↔Image美术字），0=不兼容
        /// </summary>
        private static float GetTypeMatchScore(Transform node, PicData item, float compatScore)
        {
            return PSDMatchTypeUtility.GetTypeMatchScore(node, item, compatScore);
        }

        private static float GetLayoutTypeMatchScore(Transform node, string layoutType)
        {
            return PSDMatchTypeUtility.GetLayoutTypeMatchScore(node, layoutType);
        }

        private static Dictionary<RectTransform, PSDMatchGeometry.NodeGeom> BuildNodeGeomCache(List<RectTransform> nodes, RectTransform root)
        {
            var cache = new Dictionary<RectTransform, PSDMatchGeometry.NodeGeom>();
            if (nodes == null || root == null)
            {
                return cache;
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                RectTransform node = nodes[i];
                if (node != null && !cache.ContainsKey(node))
                {
                    cache[node] = PSDMatchGeometry.ExtractNodeGeom(node, root);
                }
            }

            return cache;
        }

        private static PSDMatchGeometry.NodeGeom GetCachedNodeGeom(RectTransform node, RectTransform root, Dictionary<RectTransform, PSDMatchGeometry.NodeGeom> cache)
        {
            if (node == null)
            {
                return default;
            }

            if (cache != null && cache.TryGetValue(node, out PSDMatchGeometry.NodeGeom cached))
            {
                return cached;
            }

            PSDMatchGeometry.NodeGeom geom = PSDMatchGeometry.ExtractNodeGeom(node, root);
            if (cache != null)
            {
                cache[node] = geom;
            }

            return geom;
        }

        private static bool ShouldPruneByDistance(float distance, PSDImportConfig config)
        {
            if (config == null || !config.enableCandidatePruning || config.maxDistanceError <= 0f)
            {
                return false;
            }

            float multiplier = Mathf.Max(0.1f, config.candidateDistanceMultiplier);
            return distance > config.maxDistanceError * multiplier;
        }

        private static float ApplyHierarchyTotalMultiplier(ref ScoreBreakdown breakdown, float multiplier)
        {
            if (Mathf.Approximately(multiplier, 1f))
            {
                return breakdown.total;
            }

            multiplier = Mathf.Clamp01(multiplier);
            breakdown.core.total *= multiplier;
            return breakdown.total;
        }

        private static float GetEffectivePreLockThreshold(PSDImportConfig config, float maxScore)
        {
            if (config == null || !config.preLockUseDynamicThreshold)
            {
                return config != null ? config.preLockThreshold : 0f;
            }

            float dynamicThreshold = Mathf.Max(0f, maxScore) * Mathf.Clamp01(config.preLockDynamicRatio);
            return Mathf.Max(config.preLockDynamicMinThreshold, dynamicThreshold);
        }

        private static bool IsIdHistoryGeometryAcceptable(
            BindingPairViewModel bind,
            RectTransform savedNode,
            RectTransform rootRect,
            PSDData psdData,
            out string reason)
        {
            reason = string.Empty;
            if (bind == null || savedNode == null || rootRect == null || psdData == null)
            {
                reason = "missing geometry input";
                return false;
            }

            PSDMatchGeometry.PsdGeom psdGeom = PSDMatchGeometry.BuildPsdGeom(bind.psdItem, psdData.width, psdData.height);
            PSDMatchGeometry.NodeGeom nodeGeom = PSDMatchGeometry.ExtractNodeGeom(savedNode, rootRect);
            float iou = CalculateIou(psdGeom.rectMinLocal, psdGeom.rectMaxLocal, nodeGeom.rectMinLocal, nodeGeom.rectMaxLocal);
            float centerDistance = Vector2.Distance(psdGeom.centerLocal, nodeGeom.centerLocal);
            float sizeError = CalculateMaxSizeErrorRatio(psdGeom.sizeLocal, nodeGeom.sizeLocal);

            bool acceptable = iou >= IdHistoryMinIou ||
                              (centerDistance <= IdHistoryMaxCenterDistance &&
                               sizeError <= IdHistoryMaxSizeErrorRatio);
            if (!acceptable)
            {
                reason = $"iou={iou:F2}, center={centerDistance:F1}, sizeRatio={sizeError:F2}, geom={nodeGeom.geometrySource}";
            }

            return acceptable;
        }

        private static bool IsInsideMatchedStdPrefab(RectTransform node, HashSet<BindingPairViewModel> matchedBindings)
        {
            if (node == null || matchedBindings == null || matchedBindings.Count == 0)
            {
                return false;
            }

            foreach (BindingPairViewModel bind in matchedBindings)
            {
                if (bind == null ||
                    bind.stdPrefabMode != StdPrefabApplyMode.ReuseExisting ||
                    bind.unityNode == null ||
                    node.transform == bind.unityNode)
                {
                    continue;
                }

                if (node.transform.IsChildOf(bind.unityNode))
                {
                    return true;
                }
            }

            return false;
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

        private static void ResetMatchDiagnostics(BindingPairViewModel bind, bool clearHistoryDiagnostics = true)
        {
            if (bind == null)
            {
                return;
            }

            bind.bestCandidateScore = 0f;
            bind.secondBestCandidateScore = 0f;
            bind.scoreMargin = 0f;
            bind.isLowConfidence = false;
            bind.skippedTypeCandidates = 0;
            bind.skippedSpatialCandidates = 0;
            bind.hierarchyPenalizedCandidates = 0;
            bind.isPreLocked = false;
            if (clearHistoryDiagnostics)
            {
                bind.idHistoryRejected = false;
                bind.idHistoryRejectedPath = null;
                bind.idHistoryRejectReason = null;
            }
            bind.stdPrefabFailureReason = null;
            if (bind.stdPrefabCandidates == null)
            {
                bind.stdPrefabCandidates = new List<StdPrefabCandidateViewModel>();
            }
            else
            {
                bind.stdPrefabCandidates.Clear();
            }
        }

        private static void SetFixedMatchConfidence(BindingPairViewModel bind, float score)
        {
            if (bind == null)
            {
                return;
            }

            bind.bestCandidateScore = score;
            bind.secondBestCandidateScore = 0f;
            bind.scoreMargin = score;
            bind.isLowConfidence = false;
        }

        private static void SetUnmatchedMatchConfidence(BindingPairViewModel bind, int rowIndex, float[,] scoreMatrix, bool[,] validMatrix)
        {
            if (bind == null)
            {
                return;
            }

            GetRowTopScores(rowIndex, -1, scoreMatrix, validMatrix, out float best, out float second, out _);
            bind.bestCandidateScore = best;
            bind.secondBestCandidateScore = second;
            bind.scoreMargin = 0f;
            bind.isLowConfidence = false;
        }

        private static void SetMatchConfidence(
            BindingPairViewModel bind,
            float selectedScore,
            int rowIndex,
            int selectedCol,
            float[,] scoreMatrix,
            bool[,] validMatrix,
            PSDImportConfig config)
        {
            if (bind == null)
            {
                return;
            }

            GetRowTopScores(rowIndex, selectedCol, scoreMatrix, validMatrix, out float best, out float second, out float bestAlternative);
            bind.bestCandidateScore = best;
            bind.secondBestCandidateScore = second;
            bind.scoreMargin = bestAlternative > 0f ? selectedScore - bestAlternative : selectedScore;
            float lowConfidenceMargin = config != null ? Mathf.Max(0f, config.lowConfidenceMargin) : 0f;
            bind.isLowConfidence = selectedScore > 0f && bind.scoreMargin < lowConfidenceMargin;
        }

        private static void GetRowTopScores(
            int rowIndex,
            int selectedCol,
            float[,] scoreMatrix,
            bool[,] validMatrix,
            out float best,
            out float second,
            out float bestAlternative)
        {
            best = 0f;
            second = 0f;
            bestAlternative = 0f;

            if (scoreMatrix == null || validMatrix == null || rowIndex < 0 || rowIndex >= scoreMatrix.GetLength(0))
            {
                return;
            }

            int columns = scoreMatrix.GetLength(1);
            for (int j = 0; j < columns; j++)
            {
                if (!validMatrix[rowIndex, j])
                {
                    continue;
                }

                float score = scoreMatrix[rowIndex, j];
                if (score > best)
                {
                    second = best;
                    best = score;
                }
                else if (score > second)
                {
                    second = score;
                }

                if (j != selectedCol && score > bestAlternative)
                {
                    bestAlternative = score;
                }
            }
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
            float minScoreGap,
            float perfectThreshold,
            PSDImportConfig config,
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
            float[] rowSecondScore = new float[rowCount];
            int[] rowBestCol = new int[rowCount];
            bool[] rowPassesThreshold = new bool[rowCount];

            for (int i = 0; i < rowCount; i++)
            {
                rowBestScore[i] = -1f;
                rowSecondScore[i] = 0f;
                rowBestCol[i] = -1;
                for (int j = 0; j < nodeList.Count; j++)
                {
                    if (validMatrix[i, j] && scoreMatrix[i, j] > rowBestScore[i])
                    {
                        rowSecondScore[i] = Mathf.Max(0f, rowBestScore[i]);
                        rowBestScore[i] = scoreMatrix[i, j];
                        rowBestCol[i] = j;
                    }
                    else if (validMatrix[i, j] && scoreMatrix[i, j] > rowSecondScore[i])
                    {
                        rowSecondScore[i] = scoreMatrix[i, j];
                    }
                }
                float rowGap = rowBestScore[i] - rowSecondScore[i];
                rowPassesThreshold[i] = rowBestCol[i] >= 0 && rowBestScore[i] >= threshold && rowGap >= minScoreGap;
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

                SetMatchConfidence(bind, score, i, col, scoreMatrix, validMatrix, config);
                bool confirmed = score > perfectThreshold && !bind.isLowConfidence;
                string reason = string.Format("PreLock exclusive_optimal score={0:F0} margin={1:F1}", score, bind.scoreMargin);

                bind.unityNode = node.transform;
                bind.score = score;
                bind.isConfirmed = confirmed;
                bind.statusInfo = reason;
                bind.isIdMatched = false;
                bind.isPreLocked = true;
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
