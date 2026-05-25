using System.Collections.Generic;
using UnityEngine;

namespace PSDImporter
{
    public static class PSDMatchingStrategy
    {
        public class MatchResult
        {
            public Transform target;
            public float score;
            public string reason;
            public bool isPerfectMatch;
        }

        // ========================================================================
        // 【新增公开API】计算单个“PSD图层 vs Unity节点”的匹配得分
        // ========================================================================
        public static float CalculateMatchScore(
            PicData item,
            Transform node,
            RectTransform root,
            int psdWidth,
            int psdHeight,
            PSDImportConfig config)
        {
            // PicData is a struct, use default/null check compatible with value types
            if (item.Equals(default) || node == null || root == null || config == null)
            {
                return 0f;
            }

            RectTransform nodeRect = node as RectTransform;
            if (nodeRect == null)
            {
                return 0f;
            }

            var nodeGeom = PSDMatchGeometry.ExtractNodeGeom(nodeRect, root);
            var psdGeom = PSDMatchGeometry.BuildPsdGeom(item, psdWidth, psdHeight);
            float typeScore = PSDMatchTypeUtility.GetTypeMatchScore(node, item, config.typeCompatScore);
            return CalculateMatchScoreFromGeometry(nodeGeom, psdGeom, typeScore, config, item, nodeRect);
        }

        public static float CalculateMatchScoreFromGeometry(
            PSDMatchGeometry.NodeGeom nodeGeom,
            PSDMatchGeometry.PsdGeom psdGeom,
            float typeMatchScore,
            PSDImportConfig config,
            PicData item = default,
            RectTransform node = null)
        {
            var input = PSDMatchScoring.ScoringInput.FromConfigForItem(nodeGeom, psdGeom, typeMatchScore, config, item, node);
            return PSDMatchScoring.Evaluate(input).total;
        }

        // ========================================================================
        // 查找最佳匹配 (内部复用上面的算法)
        // ========================================================================
        public static MatchResult FindBestMatch(
            PicData item,
            Transform root,
            int psdWidth,
            int psdHeight,
            PSDImportConfig config,
            PSDBindingData bindingData,
            HashSet<Transform> occupiedNodes)
        {
            // 1. ID 优先匹配
            if (config != null && config.enableIdHistoryMatch && bindingData != null)
            {
                GameObject savedGo = bindingData.GetBindTarget(item.id);
                if (savedGo != null && savedGo.transform.IsChildOf(root))
                {
                    return new MatchResult
                    {
                        target = savedGo.transform,
                        score = 9999f,
                        reason = "ID历史绑定",
                        isPerfectMatch = true
                    };
                }
            }

            // 2. 智能遍历
            var allNodes = root.GetComponentsInChildren<RectTransform>(true);
            Transform bestCandidate = null;
            float bestScore = -1f;

            // 预计算目标位置
            RectTransform rootRect = root as RectTransform;
            if (rootRect == null)
            {
                return null;
            }

            foreach (var node in allNodes)
            {
                if (node == root) continue;
                if (occupiedNodes != null && occupiedNodes.Contains(node.transform)) continue;
                if (PSDMatchNodeFilter.ShouldSkipInactiveMatch(node, root, config)) continue;

                // 【调用核心算法】
                float totalScore = CalculateMatchScore(item, node, rootRect, psdWidth, psdHeight, config);

                if (totalScore > bestScore)
                {
                    bestScore = totalScore;
                    bestCandidate = node.transform;
                }
            }

            if (bestCandidate != null && bestScore > 1f)
            {
                return new MatchResult
                {
                    target = bestCandidate,
                    score = bestScore,
                    reason = $"智能评分: {bestScore:F0}",
                    isPerfectMatch = bestScore > 150f
                };
            }

            return null;
        }
    }
}
