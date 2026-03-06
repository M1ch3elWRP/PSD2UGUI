using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PSDImporter
{
    public static class VisualBindingRestoreService
    {
        private struct ScoreBreakdown
        {
            public float distance;
            public float diffW;
            public float diffH;
            public float scorePos;
            public float scoreSize;
            public float scoreType;
            public float weightedPos;
            public float weightedSize;
            public float weightedType;
            public float total;
            public bool passThresholds;
            public float mlProb;
            public bool mlUsed;
        }

        private class MatchCandidate
        {
            public RectTransform node;
            public float score;
            public ScoreBreakdown breakdown;
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
                bool logDetail = matchConfig.showDetailedLog;
                bool forceCandidateLog = matchConfig.forceCandidateLog;
                bool logCandidatesInLoop = logDetail && !forceCandidateLog;

                var mlConfig = matchConfig.mlConfig;
                bool useMlScore = mlConfig != null && mlConfig.useMlScore;
                PSDMatchModel mlModel = null;
                if (useMlScore)
                {
                    mlModel = PSDMatchAutoLearn.TryLoadModel(mlConfig);
                    if (mlModel == null)
                    {
                        useMlScore = false;
                        if (logDetail)
                        {
                            Debug.LogWarning("[Match] ML model not found. Fallback to manual weights.");
                        }
                    }
                }

                float perfectThreshold = useMlScore ? 80f : 150f;
                HashSet<Transform> occupiedNodes = new HashSet<Transform>();
                HashSet<BindingPairViewModel> matchedBindings = new HashSet<BindingPairViewModel>();

                if (logDetail)
                {
                    Debug.Log($"[Match] Config maxDist={matchConfig.maxDistanceError:F1}, maxSizeDiff={matchConfig.maxSizeDiff:F1}, weightPos={matchConfig.weightPosition:F2}, weightSize={matchConfig.weightSize:F2}, weightType={matchConfig.weightType:F2}, skipInactive={matchConfig.skipInactiveMatch}, forceCandidateLog={forceCandidateLog}, useMlScore={useMlScore}");
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
                        bind.statusInfo = nodeCount == 0 ? "No candidates" : "No suitable node found";
                    }
                    return;
                }

                float[,] scoreMatrix = new float[itemCount, nodeCount];
                bool[,] validMatrix = new bool[itemCount, nodeCount];
                float maxScore = 0f;
                List<int> matrixBindIndices = new List<int>();
                HashSet<BindingPairViewModel> noCandidateBinds = new HashSet<BindingPairViewModel>();

                Dictionary<RectTransform, int> nodeIndexMap = new Dictionary<RectTransform, int>();
                for (int i = 0; i < nodeList.Count; i++)
                {
                    nodeIndexMap[nodeList[i]] = i;
                }

                for (int i = 0; i < itemCount; i++)
                {
                    var bind = pendingBinds[i];
                    float localX = bind.psdItem.x - cachedPsdData.width * 0.5f;
                    float localY = bind.psdItem.y - cachedPsdData.height * 0.5f;
                    Vector3 targetWorldPos = rootTransform.TransformPoint(new Vector3(localX, localY, 0));
                    List<MatchCandidate> localCandidates = logCandidatesInLoop ? new List<MatchCandidate>() : null;
                    var candidateBuild = PSDCandidateBuilder.BuildCandidates(bind.psdItem, nodeList, rootTransform, cachedPsdData, matchConfig);
                    if (candidateBuild.candidates.Count == 0)
                    {
                        bind.statusInfo = "No candidates";
                        noCandidateBinds.Add(bind);
                    }
                    else
                    {
                        matrixBindIndices.Add(i);
                    }

                    for (int c = 0; c < candidateBuild.candidates.Count; c++)
                    {
                        var node = candidateBuild.candidates[c];
                        int j;
                        if (!nodeIndexMap.TryGetValue(node, out j))
                        {
                            continue;
                        }

                        ScoreBreakdown breakdown;
                        float score = GetMatchScore(bind.psdItem, node, targetWorldPos, matchConfig, useMlScore, mlModel, rootTransform, cachedPsdData, out breakdown);
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
                        var f = candidateBuild.filtered;
                        sb.AppendLine($"[Match] Item {bind.psdItem.pngName} (id:{bind.psdItem.id}, type:{bind.psdItem.uiType}) pos=({bind.psdItem.x:F1},{bind.psdItem.y:F1}) size=({bind.psdItem.width:F1},{bind.psdItem.height:F1}) targetWorld=({targetWorldPos.x:F1},{targetWorldPos.y:F1}) candidates={candidateBuild.candidates.Count} filtered(type:{f.type}, depth:{f.depth}, distance:{f.distance}, size:{f.size}, layout:{f.layout}) skipped(inactive:{skippedInactive}, occupied:{skippedOccupied}, root:{skippedRoot})");
                        var top = localCandidates.OrderByDescending(c => c.score).Take(5).ToList();
                        for (int k = 0; k < top.Count; k++)
                        {
                            var cand = top[k];
                            var b = cand.breakdown;
                            float nodeW = cand.node.rect.width;
                            float nodeH = cand.node.rect.height;
                            string mlInfo = b.mlUsed ? $" mlScore={cand.score:F1} mlProb={b.mlProb:F3}" : string.Empty;
                            sb.AppendLine($"  #{k + 1} {GetTransformPath(cand.node)} active={cand.node.gameObject.activeInHierarchy} nodeSize=({nodeW:F1},{nodeH:F1}) dist={b.distance:F1} diff=({b.diffW:F1},{b.diffH:F1}) scorePos={b.scorePos:F1} scoreSize={b.scoreSize:F1} scoreType={b.scoreType:F0} w=({b.weightedPos:F1},{b.weightedSize:F1},{b.weightedType:F1}) total={b.total:F1}{mlInfo}");
                        }
                        Debug.Log(sb.ToString());
                    }
                }

                if (maxScore <= 0f)
                {
                    foreach (var bind in pendingBinds)
                    {
                        if (noCandidateBinds.Contains(bind))
                        {
                            continue;
                        }
                        bind.statusInfo = "No suitable node found";
                    }
                    return;
                }

                int matrixItemCount = matrixBindIndices.Count;
                int size = Mathf.Max(matrixItemCount, nodeCount);
                float invalidCost = maxScore + 1000f;
                float[,] cost = new float[size, size];
                for (int i = 0; i < size; i++)
                {
                    for (int j = 0; j < size; j++)
                    {
                        cost[i, j] = invalidCost;
                    }
                }

                for (int mi = 0; mi < matrixItemCount; mi++)
                {
                    int i = matrixBindIndices[mi];
                    for (int j = 0; j < nodeCount; j++)
                    {
                        if (validMatrix[i, j])
                        {
                            cost[mi, j] = maxScore - scoreMatrix[i, j];
                        }
                    }
                }

                int[] assignment = PSDHungarianSolver.Solve(cost);
                for (int mi = 0; mi < matrixItemCount; mi++)
                {
                    int i = matrixBindIndices[mi];
                    int j = assignment[mi];
                    if (j < 0 || j >= nodeCount)
                    {
                        continue;
                    }

                    if (!validMatrix[i, j])
                    {
                        continue;
                    }

                    var bind = pendingBinds[i];
                    var node = nodeList[j];
                    if (occupiedNodes.Contains(node.transform))
                    {
                        continue;
                    }

                    bind.unityNode = node;
                    bind.score = scoreMatrix[i, j];
                    bind.statusInfo = $"Score: {bind.score:F0}";
                    bind.isIdMatched = false;
                    if (bind.score > perfectThreshold)
                    {
                        bind.isConfirmed = true;
                    }

                    matchedBindings.Add(bind);
                    occupiedNodes.Add(node.transform);

                    if (logDetail)
                    {
                        ScoreBreakdown breakdown;
                        Vector3 targetWorldPos = rootTransform.TransformPoint(new Vector3(bind.psdItem.x - cachedPsdData.width * 0.5f, bind.psdItem.y - cachedPsdData.height * 0.5f, 0));
                        GetMatchScore(bind.psdItem, node, targetWorldPos, matchConfig, useMlScore, mlModel, rootTransform, cachedPsdData, out breakdown);
                        float nodeW = node.rect.width;
                        float nodeH = node.rect.height;
                        string mlInfo = breakdown.mlUsed ? $" mlScore={bind.score:F1} mlProb={breakdown.mlProb:F3}" : string.Empty;
                        Debug.Log($"[Match] Result {bind.psdItem.pngName} -> {GetTransformPath(node)} score={bind.score:F1} dist={breakdown.distance:F1} diff=({breakdown.diffW:F1},{breakdown.diffH:F1}) nodeSize=({nodeW:F1},{nodeH:F1}) w=({breakdown.weightedPos:F1},{breakdown.weightedSize:F1},{breakdown.weightedType:F1}){mlInfo}");
                    }
                }

                foreach (var bind in pendingBinds)
                {
                    if (!matchedBindings.Contains(bind))
                    {
                        if (noCandidateBinds.Contains(bind))
                        {
                            bind.statusInfo = "No candidates";
                            continue;
                        }
                        bind.statusInfo = "No suitable node found";
                    }
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

            var mlConfig = config != null ? config.mlConfig : null;
            if (mlConfig != null && mlConfig.autoLearnEnabled)
            {
                PSDMatchAutoLearn.RecordAndMaybeTrain(psdPath, cachedPsdData, targetRoot.transform, bindings, config, mlConfig);
            }

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

            float localX = bind.psdItem.x - cachedPsdData.width * 0.5f;
            float localY = bind.psdItem.y - cachedPsdData.height * 0.5f;
            Vector3 targetWorldPos = rootTransform.TransformPoint(new Vector3(localX, localY, 0));

            List<MatchCandidate> localCandidates = new List<MatchCandidate>();
            var candidateBuild = PSDCandidateBuilder.BuildCandidates(bind.psdItem, nodes, rootTransform, cachedPsdData, config);
            for (int j = 0; j < candidateBuild.candidates.Count; j++)
            {
                var node = candidateBuild.candidates[j];

                ScoreBreakdown breakdown;
                float score = GetMatchScore(bind.psdItem, node, targetWorldPos, config, useMlScore, mlModel, rootTransform, cachedPsdData, out breakdown);
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
            var f = candidateBuild.filtered;
            sb.AppendLine($"[Match] Item {bind.psdItem.pngName} (id:{bind.psdItem.id}, type:{bind.psdItem.uiType}) pos=({bind.psdItem.x:F1},{bind.psdItem.y:F1}) size=({bind.psdItem.width:F1},{bind.psdItem.height:F1}) targetWorld=({targetWorldPos.x:F1},{targetWorldPos.y:F1}) candidates={localCandidates.Count} filtered(type:{f.type}, depth:{f.depth}, distance:{f.distance}, size:{f.size}, layout:{f.layout}) skipped(inactive:{skippedInactive}, occupied:{skippedOccupied}, root:{skippedRoot})");
            var top = localCandidates.OrderByDescending(c => c.score).Take(5).ToList();
            for (int k = 0; k < top.Count; k++)
            {
                var cand = top[k];
                var b = cand.breakdown;
                float nodeW = cand.node.rect.width;
                float nodeH = cand.node.rect.height;
                string mlInfo = b.mlUsed ? $" mlScore={cand.score:F1} mlProb={b.mlProb:F3}" : string.Empty;
                sb.AppendLine($"  #{k + 1} {GetTransformPath(cand.node)} active={cand.node.gameObject.activeInHierarchy} nodeSize=({nodeW:F1},{nodeH:F1}) dist={b.distance:F1} diff=({b.diffW:F1},{b.diffH:F1}) scorePos={b.scorePos:F1} scoreSize={b.scoreSize:F1} scoreType={b.scoreType:F0} w=({b.weightedPos:F1},{b.weightedSize:F1},{b.weightedType:F1}) total={b.total:F1}{mlInfo}");
            }
            Debug.Log(sb.ToString());
        }

        private static float CalculateMatchScoreDetailed(PicData item, RectTransform node, Vector3 targetWorldPos, PSDImportConfig config, out ScoreBreakdown breakdown)
        {
            breakdown = new ScoreBreakdown();
            if (config == null || node == null)
            {
                return 0f;
            }

            float dist = Vector3.Distance(node.position, targetWorldPos);
            float scorePos = 0f;
            if (dist < config.maxDistanceError)
            {
                scorePos = (1f - (dist / config.maxDistanceError)) * 100f;
            }

            float diffW = Mathf.Abs(node.rect.width - item.width);
            float diffH = Mathf.Abs(node.rect.height - item.height);
            float scoreSize = 0f;
            if ((diffW + diffH) < config.maxSizeDiff)
            {
                scoreSize = (1f - ((diffW + diffH) / config.maxSizeDiff)) * 100f;
            }

            float scoreType = PSDCandidateBuilder.IsTypeMatch(node, item.uiType) ? 100f : 0f;
            bool pass = scorePos > 0f || scoreSize > 0f;
            float weightedPos = scorePos * config.weightPosition;
            float weightedSize = scoreSize * config.weightSize;
            float weightedType = scoreType * config.weightType;
            float total = pass ? (weightedPos + weightedSize + weightedType) : 0f;

            breakdown.distance = dist;
            breakdown.diffW = diffW;
            breakdown.diffH = diffH;
            breakdown.scorePos = scorePos;
            breakdown.scoreSize = scoreSize;
            breakdown.scoreType = scoreType;
            breakdown.weightedPos = weightedPos;
            breakdown.weightedSize = weightedSize;
            breakdown.weightedType = weightedType;
            breakdown.total = total;
            breakdown.passThresholds = pass;

            return total;
        }

        private static float GetMatchScore(
            PicData item,
            RectTransform node,
            Vector3 targetWorldPos,
            PSDImportConfig config,
            bool useMlScore,
            PSDMatchModel mlModel,
            Transform rootTransform,
            PSDData cachedPsdData,
            out ScoreBreakdown breakdown)
        {
            if (!useMlScore || mlModel == null)
            {
                return CalculateMatchScoreDetailed(item, node, targetWorldPos, config, out breakdown);
            }

            float maxDist = mlModel.maxDistanceError > 0f ? mlModel.maxDistanceError : config.maxDistanceError;
            float maxSize = mlModel.maxSizeDiff > 0f ? mlModel.maxSizeDiff : config.maxSizeDiff;
            int maxDepth = mlModel.maxDepthDiff > 0 ? mlModel.maxDepthDiff : (config != null && config.mlConfig != null ? config.mlConfig.maxDepthDiff : 10);

            float[] x = PSDMatchFeatureExtractor.ExtractFeatures(item, node, rootTransform, cachedPsdData.width, cachedPsdData.height, maxDist, maxSize, maxDepth);
            float prob = PSDMatchML.Predict(mlModel, x);
            float score = prob * 100f;

            CalculateMatchScoreDetailed(item, node, targetWorldPos, config, out breakdown);
            breakdown.mlProb = prob;
            breakdown.mlUsed = true;
            return score;
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
