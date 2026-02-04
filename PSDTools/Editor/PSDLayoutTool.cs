using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace PSDImporter
{
    public static class PSDLayoutTool
    {
        /// <summary>
        /// 核心入口：应用 LayoutGroup 设置
        /// </summary>
        public static void ApplyLayoutGroup(GameObject groupGo, PicData groupData, List<PicData> childrenPSD)
        {
            if (childrenPSD == null || childrenPSD.Count == 0)
            {
                Debug.LogWarning($"[PSDLayoutTool] Group '{groupGo.name}' 没有找到子节点数据，无法计算 Layout。");
                return;
            }

            // 必须先开启 RectTransform，否则 LayoutGroup 报错
            var rt = groupGo.GetComponent<RectTransform>();
            if (rt == null) rt = groupGo.AddComponent<RectTransform>();

            // 根据 Layout 类型分发
            if (groupData.layoutType == "Horizontal")
            {
                SetupLinear(groupGo, groupData, childrenPSD, true);
            }
            else if (groupData.layoutType == "Vertical")
            {
                SetupLinear(groupGo, groupData, childrenPSD, false);
            }
            else if (groupData.layoutType == "Grid")
            {
                SetupGrid(groupGo, groupData, childrenPSD);
            }
        }

        // --- 线性布局 (Horizontal / Vertical) ---
        private static void SetupLinear(GameObject go, PicData group, List<PicData> children, bool isHorizontal)
        {
            // 1. 获取或添加组件
            var lg = isHorizontal ?
                EnsureComponent<HorizontalLayoutGroup>(go) as HorizontalOrVerticalLayoutGroup :
                EnsureComponent<VerticalLayoutGroup>(go) as HorizontalOrVerticalLayoutGroup;

            // 2. 排序 (确保按照视觉顺序计算间距)
            if (isHorizontal)
                children.Sort((a, b) => a.x.CompareTo(b.x)); // 从左到右
            else
                children.Sort((a, b) => b.y.CompareTo(a.y)); // 从上到下 (PSD y 为自下向上)

            // 3. 计算 Padding (基于第一个元素的偏移)
            // 公式：Padding = 子元素边界 - 父元素边界
            // 注意：PSD 坐标是中心点，且 Y 轴为自下向上
            float groupLeft = GetLeft(group);
            float groupTop = GetTop(group);

            float firstLeft = GetLeft(children[0]);
            float firstTop = GetTop(children[0]);

            int paddingLeft = Mathf.Max(0, Mathf.RoundToInt(firstLeft - groupLeft));
            int paddingTop = Mathf.Max(0, Mathf.RoundToInt(groupTop - firstTop));

            lg.padding = new RectOffset(paddingLeft, 0, paddingTop, 0);

            // 4. 计算 Spacing (间距)
            if (children.Count > 1)
            {
                if (isHorizontal)
                {
                    // Spacing = Item2.Left - Item1.Right
                    float item1Right = GetRight(children[0]);
                    float item2Left = GetLeft(children[1]);
                    lg.spacing = item2Left - item1Right;
                }
                else
                {
                    // Spacing = Item1.Bottom - Item2.Top
                    float item1Bottom = GetBottom(children[0]);
                    float item2Top = GetTop(children[1]);
                    lg.spacing = item1Bottom - item2Top;
                }
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
        private static void SetupGrid(GameObject go, PicData group, List<PicData> children)
        {
            var glg = EnsureComponent<GridLayoutGroup>(go);

            // 1. Cell Size (取第一个子物体的大小)
            glg.cellSize = new Vector2(children[0].width, children[0].height);

            // 2. 计算 Spacing (如果有至少2个物体)
            // 假设横向排列，找到X坐标明显不同的第二个物体
            children.Sort((a, b) => a.x.CompareTo(b.x));

            if (children.Count > 1)
            {
                // 横向间距
                float item1Right = GetRight(children[0]);
                float item2Left = GetLeft(children[1]);
                float spacingX = Mathf.Max(0, item2Left - item1Right);

                // 纵向间距 (尝试找Y不同的)
                float spacingY = spacingX; // 默认相等
                var row2Item = children.FirstOrDefault(c => Mathf.Abs(c.y - children[0].y) > 10f);
                if (row2Item.width > 0)
                {
                    float row1Bottom = GetBottom(children[0]);
                    float row2Top = GetTop(row2Item);
                    spacingY = Mathf.Max(0, row1Bottom - row2Top);
                }

                glg.spacing = new Vector2(spacingX, spacingY);
            }

            // 3. 基础设置
            glg.startCorner = GridLayoutGroup.Corner.UpperLeft;
            glg.childAlignment = TextAnchor.UpperLeft;
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
    }
}
