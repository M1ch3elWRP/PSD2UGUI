using UnityEngine;
using UnityEditor;

namespace PSDImporter
{
    public static class PSDGroupTool
    {
        /// <summary>
        /// 递归清洗层级：将所有 Container 节点移动到子物体的中心点
        /// </summary>
        public static void AlignGroups(Transform root)
        {
            if (root == null) return;

            // 后序遍历：先处理最底层的子节点，再处理父节点
            // 这样保证父节点计算包围盒时，子节点已经是归位好的状态
            for (int i = 0; i < root.childCount; i++)
            {
                AlignGroups(root.GetChild(i));
            }

            // 判断是否为“组节点”
            // 判定标准：有子节点 && 自己身上没有渲染组件(UISprite/UILabel)
            bool isGroup = root.childCount > 0 &&
                           root.GetComponent<UISprite>() == null &&
                           root.GetComponent<UILabel>() == null;

            // 根节点（通常是 UIRoot）不要动
            if (root.parent == null || root.GetComponent<UIRoot>() != null) isGroup = false;

            if (isGroup)
            {
                FitRectTransformToChildren(root as RectTransform);
            }
        }

        private static void FitRectTransformToChildren(RectTransform parent)
        {
            if (parent == null || parent.childCount == 0) return;

            Undo.RecordObject(parent, "Fit Group");
            foreach (Transform child in parent) Undo.RecordObject(child, "Fit Group Child");

            // 1. 记录所有子节点的世界坐标 (World Position)
            // 因为移动父节点会带动子节点，我们需要最后把子节点挪回去
            Vector3[] originalChildWorldPos = new Vector3[parent.childCount];
            for (int i = 0; i < parent.childCount; i++)
            {
                originalChildWorldPos[i] = parent.GetChild(i).position;
            }

            // 2. 计算子节点的世界包围盒中心
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, 0);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, 0);
            bool hasValidChild = false;

            for (int i = 0; i < parent.childCount; i++)
            {
                RectTransform child = parent.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeSelf) continue;

                // 获取子节点的四个角的世界坐标
                Vector3[] corners = new Vector3[4];
                child.GetWorldCorners(corners);

                foreach (var p in corners)
                {
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                hasValidChild = true;
            }

            if (!hasValidChild) return;

            Vector3 centerWorldPos = (min + max) * 0.5f;
            Vector2 newSize = new Vector2(max.x - min.x, max.y - min.y);

            // 3. 移动父节点到中心
            // 保持 Pivot 不变，或者强制 Pivot 居中 (0.5, 0.5) 方便适配
            // 建议：如果只是想归位，不改 Pivot；如果想标准化，强制 Pivot=0.5
            // 这里我们保持 Pivot 不变，只改位置
            parent.position = centerWorldPos;

            // 4. (可选) 设置父节点的大小为包围盒大小
            // 注意：SetSizeWithCurrentAnchors 不会改变 Scale，但会改变 rect.width/height
            // 如果你的组节点有缩放，这步可能需要小心
            if (parent.localScale == Vector3.one)
            {
                parent.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, newSize.x);
                parent.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, newSize.y);
            }

            // 5. 恢复子节点的世界坐标
            // 这一步至关重要：父节点动了，子节点视觉上不能动
            for (int i = 0; i < parent.childCount; i++)
            {
                parent.GetChild(i).position = originalChildWorldPos[i];
            }

            // 6. (进阶) 如果需要，重置子节点的锚点为中心
            // 这一步取决于你的适配策略，通常归位后，子节点的 localPosition 会变得很小
        }
    }
}