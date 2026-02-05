using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

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
            Vector3 targetWorldPos, // 为了性能，位置由外部算好传入，或者在内部算
            PSDImportConfig config)
        {
            // A. 位置分
            float scorePos = 0f;
            float dist = Vector3.Distance(node.position, targetWorldPos);

            // 只有在阈值内才给分
            if (dist < config.maxDistanceError)
                scorePos = (1f - (dist / config.maxDistanceError)) * 100f;

            // B. 尺寸分
            float scoreSize = 0f;
            float diffW = Mathf.Abs(((RectTransform)node).rect.width - item.width);
            float diffH = Mathf.Abs(((RectTransform)node).rect.height - item.height);

            if ((diffW + diffH) < config.maxSizeDiff)
                scoreSize = (1f - ((diffW + diffH) / config.maxSizeDiff)) * 100f;

            // C. 类型分
            float scoreType = IsTypeMatch(node, item.uiType) ? 100f : 0f;

            // D. 综合算分
            // 只有当位置或尺寸至少有一项匹配时，才计算总分（防止离谱的误配）
            if (scorePos > 0 || scoreSize > 0)
            {
                return (scorePos * config.weightPosition) +
                       (scoreSize * config.weightSize) +
                       (scoreType * config.weightType);
            }

            return 0f;
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
            if (bindingData != null)
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
            float localX = item.x - psdWidth * 0.5f;
            float localY = item.y - psdHeight * 0.5f;
            Vector3 targetWorldPos = root.TransformPoint(new Vector3(localX, localY, 0));

            foreach (var node in allNodes)
            {
                if (node == root) continue;
                if (occupiedNodes != null && occupiedNodes.Contains(node.transform)) continue;
                if (config != null && config.skipInactiveMatch && !node.gameObject.activeInHierarchy) continue;

                // 【调用核心算法】
                float totalScore = CalculateMatchScore(item, node, targetWorldPos, config);

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
    }
}
