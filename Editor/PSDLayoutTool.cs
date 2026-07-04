using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PSDImporter
{
    public static class PSDLayoutTool
    {
        public static void ApplyLayoutGroup(GameObject groupGo, PicData groupData, List<PicData> childrenPSD, PSDImportConfig config = null)
        {
            if (childrenPSD == null || childrenPSD.Count == 0)
            {
                Debug.LogWarning($"[PSDLayoutTool] Group '{groupGo.name}' 没有找到子节点数据，无法计算 Layout。");
                return;
            }

            ComputeEffectiveGroupBounds(groupData, childrenPSD);

            var rt = groupGo.GetComponent<RectTransform>();
            if (rt == null) rt = groupGo.AddComponent<RectTransform>();

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

        private static void SetupLinear(GameObject go, PicData group, List<PicData> children, bool isHorizontal, PSDImportConfig config)
        {
            var table = EnsureComponent<UITable>(go);
            table.direction = isHorizontal ? UITable.Direction.Horizontal : UITable.Direction.Vertical;
            table.columns = 0;

            if (isHorizontal)
                children.Sort((a, b) => a.x.CompareTo(b.x));
            else
                children.Sort((a, b) => b.y.CompareTo(a.y));

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

            Vector2 spacing = Vector2.zero;
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
                int sp = Mathf.RoundToInt(Median(spacings));
                if (isHorizontal) spacing.x = Mathf.Max(0, sp);
                else spacing.y = Mathf.Max(0, sp);
            }

            table.padding = new Vector2(paddingLeft, paddingTop);
            table.cellSpacing = spacing;
            table.repositionNow = true;
            NGUITools.MarkParentChanged();
        }

        private static void SetupGrid(GameObject go, PicData group, List<PicData> children, PSDImportConfig config)
        {
            var grid = EnsureComponent<UIGrid>(go);

            var sortedByX = children.OrderBy(c => c.x).ToList();
            var columns = ClusterByX(sortedByX, tolerance: 5f);

            int columnCount = Mathf.Max(1, columns.Count);
            grid.columns = columnCount;
            grid.arrangement = UIGrid.Arrangement.Horizontal;

            Vector2 cellSize = ComputeModeCellSize(children);
            grid.cellWidth = cellSize.x;
            grid.cellHeight = cellSize.y;

            if (children.Count > 1 && columns.Count >= 2)
            {
                var spacingXList = new List<float>();
                for (int ci = 1; ci < columns.Count; ci++)
                {
                    float prevColRight = columns[ci - 1].Max(c => GetRight(c));
                    float curColLeft   = columns[ci].Min(c => GetLeft(c));
                    spacingXList.Add(curColLeft - prevColRight);
                }
                float spacingX = spacingXList.Count > 0 ? Mathf.RoundToInt(Median(spacingXList)) : 0;

                var sortedByY = children.OrderBy(c => c.y).ToList();
                var rows = ClusterByY(sortedByY, tolerance: 5f);
                var spacingYList = new List<float>();
                if (rows.Count >= 2)
                {
                    for (int ri = 1; ri < rows.Count; ri++)
                    {
                        float prevRowBottom = rows[ri - 1].Min(c => GetBottom(c));
                        float curRowTop     = rows[ri].Max(c => GetTop(c));
                        spacingYList.Add(prevRowBottom - curRowTop);
                    }
                }
                float spacingY = spacingYList.Count > 0 ? Mathf.RoundToInt(Median(spacingYList)) : 0;

                // NGUI UIGrid 无独立 spacing 字段：把间距折算进 cellWidth/cellHeight
                grid.cellWidth = cellSize.x + Mathf.Max(0, spacingX);
                grid.cellHeight = cellSize.y + Mathf.Max(0, spacingY);
            }

            float groupLeft   = GetLeft(group);
            float groupRight  = GetRight(group);
            float groupTop    = GetTop(group);
            float groupBottom = GetBottom(group);

            float minLeft   = children.Min(c => GetLeft(c));
            float maxRight  = children.Max(c => GetRight(c));
            float maxTop    = children.Max(c => GetTop(c));
            float minBottom = children.Min(c => GetBottom(c));

            int paddingLeft   = Mathf.Max(0, Mathf.RoundToInt(minLeft - groupLeft));
            int paddingTop    = Mathf.Max(0, Mathf.RoundToInt(groupTop - maxTop));

            // UIGrid 没有四边 padding，靠 transform localPosition 偏移补偿左上边距
            Vector3 origPos = go.transform.localPosition;
            go.transform.localPosition = new Vector3(origPos.x + paddingLeft * 0.5f, origPos.y - paddingTop * 0.5f, origPos.z);

            grid.repositionNow = true;
            NGUITools.MarkParentChanged();
        }

        private static void RemoveOtherLayoutGroups(GameObject go, System.Type keepType)
        {
            var tables = go.GetComponents<UITable>();
            for (int i = 0; i < tables.Length; i++)
            {
                if (tables[i] != null && tables[i].GetType() != keepType)
                {
                    Object.DestroyImmediate(tables[i]);
                }
            }
            var grids = go.GetComponents<UIGrid>();
            for (int i = 0; i < grids.Length; i++)
            {
                if (grids[i] != null && grids[i].GetType() != keepType)
                {
                    Object.DestroyImmediate(grids[i]);
                }
            }
        }

        private static T EnsureComponent<T>(GameObject go) where T : Component
        {
            T comp = go.GetComponent<T>();
            if (comp == null)
            {
                RemoveOtherLayoutGroups(go, typeof(T));
                comp = go.AddComponent<T>();
            }
            return comp;
        }

        private static float GetLeft(PicData item) => item.x - item.width / 2f;
        private static float GetRight(PicData item) => item.x + item.width / 2f;
        private static float GetTop(PicData item) => item.y + item.height / 2f;
        private static float GetBottom(PicData item) => item.y - item.height / 2f;

        public static void ComputeEffectiveGroupBounds(PicData groupData, List<PicData> children)
        {
            if (children == null || children.Count == 0) return;

            float effLeft   = children.Min(c => GetLeft(c));
            float effRight  = children.Max(c => GetRight(c));
            float effTop    = children.Max(c => GetTop(c));
            float effBottom = children.Min(c => GetBottom(c));

            float effWidth  = effRight - effLeft;
            float effHeight = effTop - effBottom;

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

        private static Vector2 ComputeModeCellSize(List<PicData> children)
        {
            if (children.Count == 0) return Vector2.zero;

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
