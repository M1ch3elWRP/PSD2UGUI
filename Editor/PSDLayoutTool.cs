using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PSDImporter
{
    public static class PSDLayoutTool
    {
        /// <summary>
        /// 核心入口：应用 LayoutGroup 设置
        /// </summary>
        public static void ApplyLayoutGroup(GameObject groupGo, PicData groupData, List<PicData> childrenPSD, PSDImportConfig config = null)
        {
            if (childrenPSD == null || childrenPSD.Count == 0)
            {
                Debug.LogWarning($"[PSDLayoutTool] Group '{groupGo.name}' 没有找到子节点数据，无法计算 Layout。");
                return;
            }

            // 【自校正】从子节点反推组的"有效包围盒"，覆盖 PSD 可能不准的原始值
            // 因为容器 RectTransform 的尺寸已按 PSD groupData.width/height 设好，
            // 此处仅用子节点并集微调 groupData 的 x/y/width/height，使 Padding 计算更准
            ComputeEffectiveGroupBounds(groupData, childrenPSD);

            // 必须先开启 RectTransform，否则 LayoutGroup 报错
            var rt = groupGo.GetComponent<RectTransform>();
            if (rt == null) rt = groupGo.AddComponent<RectTransform>();

            // 根据 Layout 类型分发
            if (groupData.layoutType == "Horizontal")
            {
                SetupLinear(groupGo, groupData, childrenPSD, true, config);
            }
            else if (groupData.layoutType == "Vertical")
            {
                SetupLinear(groupGo, groupData, childrenPSD, false, config);
            }
            else if (groupData.layoutType == "Grid")
            {
                SetupGrid(groupGo, groupData, childrenPSD, config);
            }
        }

        // --- 线性布局 (Horizontal / Vertical) ---
        private static void SetupLinear(GameObject go, PicData group, List<PicData> children, bool isHorizontal, PSDImportConfig config)
        {
            // 1. 获取或添加组件
            var lg = isHorizontal ?
                EnsureLayoutComponent<HorizontalLayoutGroup>(go, config != null ? config.horizontalLayoutComponent : null) as HorizontalOrVerticalLayoutGroup :
                EnsureLayoutComponent<VerticalLayoutGroup>(go, config != null ? config.verticalLayoutComponent : null) as HorizontalOrVerticalLayoutGroup;

            // 2. 排序 (确保按照视觉顺序计算间距)
            if (isHorizontal)
                children.Sort((a, b) => a.x.CompareTo(b.x)); // 从左到右
            else
                children.Sort((a, b) => b.y.CompareTo(a.y)); // 从上到下 (PSD y 为自下向上)

            // 3. 计算 Padding — 四边从子元素极值反推
            // Padding = 子元素并集边界到容器边界的距离
            float groupLeft   = GetLeft(group);
            float groupRight  = GetRight(group);
            float groupTop    = GetTop(group);
            float groupBottom = GetBottom(group);

            float minLeft   = children.Min(c => GetLeft(c));
            float maxRight  = children.Max(c => GetRight(c));
            float maxTop    = children.Max(c => GetTop(c));
            float minBottom = children.Min(c => GetBottom(c));

            int paddingLeft   = Mathf.Max(0, Mathf.RoundToInt(minLeft - groupLeft));
            int paddingRight  = Mathf.Max(0, Mathf.RoundToInt(groupRight - maxRight));
            int paddingTop    = Mathf.Max(0, Mathf.RoundToInt(groupTop - maxTop));
            int paddingBottom = Mathf.Max(0, Mathf.RoundToInt(minBottom - groupBottom));

            lg.padding = new RectOffset(paddingLeft, paddingRight, paddingTop, paddingBottom);

            // 4. 计算 Spacing — 取所有相邻间距的中位数（抗单点异常）
            if (children.Count > 1)
            {
                var spacings = new List<float>(children.Count - 1);
                if (isHorizontal)
                {
                    for (int i = 1; i < children.Count; i++)
                        spacings.Add(GetLeft(children[i]) - GetRight(children[i - 1]));
                }
                else
                {
                    for (int i = 1; i < children.Count; i++)
                        spacings.Add(GetBottom(children[i - 1]) - GetTop(children[i]));
                }
                lg.spacing = Mathf.RoundToInt(Median(spacings));
            }
            else
            {
                lg.spacing = 0;
            }

            // 5. 基础设置 (防止自动拉伸破坏原始尺寸)
            lg.childAlignment = TextAnchor.UpperLeft;
            lg.childControlWidth = false;
            lg.childControlHeight = false;
            lg.childForceExpandWidth = false;
            lg.childForceExpandHeight = false;
        }

        // --- 网格布局 (Grid) ---
        private static void SetupGrid(GameObject go, PicData group, List<PicData> children, PSDImportConfig config)
        {
            var glg = EnsureLayoutComponent<GridLayoutGroup>(go, config != null ? config.gridLayoutComponent : null);

            // 1. 按 X 坐标排序后聚类（容差 ±5px），推断列数
            var sortedByX = children.OrderBy(c => c.x).ToList();
            var columns = ClusterByX(sortedByX, tolerance: 5f);

            int columnCount = Mathf.Max(1, columns.Count);
            glg.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            glg.constraintCount = columnCount;

            // 2. CellSize 取众数尺寸（先收集所有尺寸，取出现最多的）
            glg.cellSize = ComputeModeCellSize(children);

            // 3. 行列间距用中位数
            if (children.Count > 1 && columns.Count >= 2)
            {
                // 列间水平间距：取每对相邻列的左列最右元素与右列最左元素间距
                var spacingXList = new List<float>();
                for (int ci = 1; ci < columns.Count; ci++)
                {
                    float prevColRight = columns[ci - 1].Max(c => GetRight(c));
                    float curColLeft   = columns[ci].Min(c => GetLeft(c));
                    spacingXList.Add(curColLeft - prevColRight);
                }
                float spacingX = spacingXList.Count > 0 ? Mathf.RoundToInt(Median(spacingXList)) : 0;

                // 行间垂直间距：按 Y 坐标排列后取相邻行间距中位数
                var sortedByY = children.OrderBy(c => c.y).ToList();
                // 按 Y 聚类分行（容差同列聚类）
                var rows = ClusterByY(sortedByY, tolerance: 5f);
                var spacingYList = new List<float>();
                if (rows.Count >= 2)
                {
                    for (int ri = 1; ri < rows.Count; ri++)
                    {
                        float prevRowBottom = rows[ri - 1].Min(c => GetBottom(c)); // PSD Y向上，bottom值更小
                        float curRowTop     = rows[ri].Max(c => GetTop(c));
                        spacingYList.Add(prevRowBottom - curRowTop);
                    }
                }
                float spacingY = spacingYList.Count > 0 ? Mathf.RoundToInt(Median(spacingYList)) : 0;

                glg.spacing = new Vector2(Mathf.Max(0, spacingX), Mathf.Max(0, spacingY));
            }
            else
            {
                glg.spacing = Vector2.zero;
            }

            // 4. 四边 Padding
            float groupLeft   = GetLeft(group);
            float groupRight  = GetRight(group);
            float groupTop    = GetTop(group);
            float groupBottom = GetBottom(group);

            float minLeft   = children.Min(c => GetLeft(c));
            float maxRight  = children.Max(c => GetRight(c));
            float maxTop    = children.Max(c => GetTop(c));
            float minBottom = children.Min(c => GetBottom(c));

            int paddingLeft   = Mathf.Max(0, Mathf.RoundToInt(minLeft - groupLeft));
            int paddingRight  = Mathf.Max(0, Mathf.RoundToInt(groupRight - maxRight));
            int paddingTop    = Mathf.Max(0, Mathf.RoundToInt(groupTop - maxTop));
            int paddingBottom = Mathf.Max(0, Mathf.RoundToInt(minBottom - groupBottom));

            glg.padding = new RectOffset(paddingLeft, paddingRight, paddingTop, paddingBottom);

            // 5. 基础设置
            glg.startCorner = GridLayoutGroup.Corner.UpperLeft;
            glg.childAlignment = TextAnchor.UpperLeft;
        }

        private static T EnsureLayoutComponent<T>(GameObject go, MonoScript overrideScript) where T : Component
        {
            var overrideType = GetOverrideType<T>(overrideScript);
            if (overrideType != null)
            {
                RemoveOtherLayoutGroups(go, overrideType);
                var comp = go.GetComponent(overrideType) as T;
                if (comp == null) comp = go.AddComponent(overrideType) as T;
                return comp;
            }
            return EnsureComponent<T>(go);
        }

        private static System.Type GetOverrideType<T>(MonoScript script) where T : Component
        {
            if (script == null) return null;
            var type = script.GetClass();
            if (type == null) return null;
            if (!typeof(T).IsAssignableFrom(type)) return null;
            return type;
        }

        private static void RemoveOtherLayoutGroups(GameObject go, System.Type keepType)
        {
            var layoutGroups = go.GetComponents<LayoutGroup>();
            for (int i = 0; i < layoutGroups.Length; i++)
            {
                if (layoutGroups[i] != null && layoutGroups[i].GetType() != keepType)
                {
                    Object.DestroyImmediate(layoutGroups[i]);
                }
            }
        }

        private static T EnsureComponent<T>(GameObject go) where T : Component
        {
            T comp = go.GetComponent<T>();
            if (comp == null) comp = go.AddComponent<T>();
            return comp;
        }

        private static float GetLeft(PicData item) => item.x - item.width / 2f;
        private static float GetRight(PicData item) => item.x + item.width / 2f;
        private static float GetTop(PicData item) => item.y + item.height / 2f;
        private static float GetBottom(PicData item) => item.y - item.height / 2f;

        // --- 自校正：从子节点反推组的有效包围盒 ---
        /// <summary>
        /// 用子节点并集边界覆盖 groupData 的 x/y/width/height，
        /// 解决 PSD 导出的组 bounds 因隐藏层残留、混合模式溢出等偏大的问题。
        /// 仅当子节点并集面积小于原始组面积时才缩紧，不会扩大。
        /// </summary>
        public static void ComputeEffectiveGroupBounds(PicData groupData, List<PicData> children)
        {
            if (children == null || children.Count == 0) return;

            float effLeft   = children.Min(c => GetLeft(c));
            float effRight  = children.Max(c => GetRight(c));
            float effTop    = children.Max(c => GetTop(c));
            float effBottom = children.Min(c => GetBottom(c));

            float effWidth  = effRight - effLeft;
            float effHeight = effTop - effBottom;

            // 只有当子节点并集确实比原始 bounds 更紧时才覆盖
            float origArea = groupData.width * groupData.height;
            float effArea  = effWidth * effHeight;
            if (effArea < origArea && effWidth > 0 && effHeight > 0)
            {
                groupData.x = (effLeft + effRight) / 2f;
                groupData.y = (effBottom + effTop) / 2f;
                groupData.width = effWidth;
                groupData.height = effHeight;
            }
        }

        // --- X 坐标聚类：按 x 排序后容差内归为同列 ---
        private static List<List<PicData>> ClusterByX(List<PicData> sorted, float tolerance)
        {
            var result = new List<List<PicData>>();
            if (sorted.Count == 0) return result;

            var currentCol = new List<PicData> { sorted[0] };
            float currentX = sorted[0].x;

            for (int i = 1; i < sorted.Count; i++)
            {
                if (Mathf.Abs(sorted[i].x - currentX) <= tolerance)
                {
                    currentCol.Add(sorted[i]);
                }
                else
                {
                    result.Add(currentCol);
                    currentCol = new List<PicData> { sorted[i] };
                    currentX = sorted[i].x;
                }
            }
            result.Add(currentCol);
            return result;
        }

        // --- Y 坐标聚类：按 y 排序后容差内归为同行 ---
        private static List<List<PicData>> ClusterByY(List<PicData> sorted, float tolerance)
        {
            var result = new List<List<PicData>>();
            if (sorted.Count == 0) return result;

            var currentRow = new List<PicData> { sorted[0] };
            float currentY = sorted[0].y;

            for (int i = 1; i < sorted.Count; i++)
            {
                if (Mathf.Abs(sorted[i].y - currentY) <= tolerance)
                {
                    currentRow.Add(sorted[i]);
                }
                else
                {
                    result.Add(currentRow);
                    currentRow = new List<PicData> { sorted[i] };
                    currentY = sorted[i].y;
                }
            }
            result.Add(currentRow);
            return result;
        }

        // --- 众数 CellSize：收集所有子元素尺寸，取出现最多的 ---
        private static Vector2 ComputeModeCellSize(List<PicData> children)
        {
            if (children.Count == 0) return Vector2.zero;

            // 将尺寸量化到整数格，收集频率
            var freq = new Dictionary<string, int>();
            var sizeMap = new Dictionary<string, Vector2>();
            foreach (var c in children)
            {
                int w = Mathf.RoundToInt(c.width);
                int h = Mathf.RoundToInt(c.height);
                string key = $"{w}x{h}";
                if (!freq.ContainsKey(key))
                {
                    freq[key] = 0;
                    sizeMap[key] = new Vector2(w, h);
                }
                freq[key]++;
            }

            string modeKey = null;
            int maxFreq = 0;
            foreach (var kv in freq)
            {
                if (kv.Value > maxFreq)
                {
                    maxFreq = kv.Value;
                    modeKey = kv.Key;
                }
            }
            return modeKey != null ? sizeMap[modeKey] : new Vector2(children[0].width, children[0].height);
        }

        // --- 中位数 ---
        private static float Median(List<float> values)
        {
            if (values == null || values.Count == 0) return 0f;
            var sorted = new List<float>(values);
            sorted.Sort();
            int mid = sorted.Count / 2;
            if (sorted.Count % 2 == 0)
                return (sorted[mid - 1] + sorted[mid]) / 2f;
            else
                return sorted[mid];
        }
    }
}
