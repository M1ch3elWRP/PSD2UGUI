using System;
using UnityEngine;
using UnityEngine.UI;

namespace PSDImporter
{
    internal static class PSDMatchNodeFilter
    {
        internal static bool ShouldSkipInactiveMatch(Transform node, Transform matchRoot, PSDImportConfig config)
        {
            if (config == null || !config.skipInactiveMatch || node == null)
            {
                return false;
            }

            // Restore targets are often closed UI windows. Ignore inactive state from
            // the selected root or its parents, but still skip nodes disabled inside it.
            Transform current = node;
            while (current != null && current != matchRoot)
            {
                if (!current.gameObject.activeSelf)
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        internal static string FormatActiveState(GameObject go)
        {
            if (go == null)
            {
                return "active=null";
            }

            return $"activeSelf={go.activeSelf} activeInHierarchy={go.activeInHierarchy}";
        }
    }

    internal static class PSDMatchTypeUtility
    {
        internal static float GetTypeMatchScore(Transform node, PicData item, float compatScore)
        {
            if (node == null)
            {
                return 0f;
            }

            string psdType = item.uiType;
            if (string.Equals(psdType, "Button", StringComparison.OrdinalIgnoreCase))
            {
                if (node.GetComponent<Button>() != null) return 100f;
                if (node.GetComponent<Toggle>() != null) return 80f;
                if (node.GetComponent<Slider>() != null ||
                    node.GetComponent<Scrollbar>() != null ||
                    node.GetComponent<InputField>() != null ||
                    node.GetComponent<Dropdown>() != null)
                {
                    return 60f;
                }

                return node.GetComponent<Selectable>() != null ? 60f : 0f;
            }

            if (string.Equals(psdType, "Text", StringComparison.OrdinalIgnoreCase))
            {
                if (node.GetComponent<Text>() != null) return 100f;
                return compatScore > 0f && node.GetComponent<Image>() != null && node.GetComponent<Selectable>() == null
                    ? compatScore
                    : 0f;
            }

            if (string.Equals(psdType, "RawImage", StringComparison.OrdinalIgnoreCase))
            {
                return node.GetComponent<RawImage>() != null ? 100f : 0f;
            }

            if (string.Equals(psdType, "Image", StringComparison.OrdinalIgnoreCase))
            {
                if (node.GetComponent<Image>() != null)
                {
                    return node.GetComponent<Selectable>() == null ? 100f : 60f;
                }

                return compatScore > 0f && node.GetComponent<Text>() != null ? compatScore : 0f;
            }

            if (string.Equals(psdType, "Layout", StringComparison.OrdinalIgnoreCase))
            {
                return GetLayoutTypeMatchScore(node, item.layoutType);
            }

            if (string.Equals(psdType, "ScrollRect", StringComparison.OrdinalIgnoreCase))
            {
                return PSDScrollRectUtility.IsScrollRectNode(node) ? 100f : 0f;
            }

            if (string.Equals(psdType, "Item", StringComparison.OrdinalIgnoreCase))
            {
                return node.GetComponent<LayoutGroup>() == null && node.GetComponentInParent<LayoutGroup>() != null ? 100f : 0f;
            }

            return 0f;
        }

        internal static bool IsTypeMatch(Transform node, PicData item, float compatScore = 0f)
        {
            return GetTypeMatchScore(node, item, compatScore) > 0f;
        }

        internal static bool IsTypeMatch(Transform node, string psdType, float compatScore = 0f)
        {
            PicData item = default;
            item.uiType = psdType;
            return IsTypeMatch(node, item, compatScore);
        }

        internal static float GetLayoutTypeMatchScore(Transform node, string layoutType)
        {
            if (node == null)
            {
                return 0f;
            }

            if (string.Equals(layoutType, "Horizontal", StringComparison.OrdinalIgnoreCase))
            {
                return node.GetComponent<HorizontalLayoutGroup>() != null ? 100f : 0f;
            }

            if (string.Equals(layoutType, "Vertical", StringComparison.OrdinalIgnoreCase))
            {
                return node.GetComponent<VerticalLayoutGroup>() != null ? 100f : 0f;
            }

            if (string.Equals(layoutType, "Grid", StringComparison.OrdinalIgnoreCase))
            {
                return node.GetComponent<GridLayoutGroup>() != null ? 100f : 0f;
            }

            return node.GetComponent<LayoutGroup>() != null ? 100f : 0f;
        }
    }

    internal static class PSDTagUtility
    {
        internal static bool HasTag(string rawName, string tag)
        {
            if (string.IsNullOrEmpty(rawName) || string.IsNullOrEmpty(tag))
            {
                return false;
            }

            if (tag[0] != '@')
            {
                tag = "@" + tag;
            }

            int index = rawName.IndexOf('@');
            while (index >= 0)
            {
                int end = index + 1;
                while (end < rawName.Length && IsTagChar(rawName[end]))
                {
                    end++;
                }

                if (end > index + 1 &&
                    string.Equals(rawName.Substring(index, end - index), tag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                index = rawName.IndexOf('@', index + 1);
            }

            return false;
        }

        internal static bool HasAnyTag(string rawName, params string[] tags)
        {
            if (tags == null)
            {
                return false;
            }

            for (int i = 0; i < tags.Length; i++)
            {
                if (HasTag(rawName, tags[i]))
                {
                    return true;
                }
            }

            return false;
        }

        internal static string RemoveTags(string rawName, params string[] tags)
        {
            if (string.IsNullOrEmpty(rawName) || tags == null)
            {
                return rawName;
            }

            string result = rawName;
            for (int i = 0; i < tags.Length; i++)
            {
                string tag = tags[i];
                if (string.IsNullOrEmpty(tag))
                {
                    continue;
                }

                if (tag[0] != '@')
                {
                    tag = "@" + tag;
                }

                result = RemoveTagToken(result, tag);
            }

            return result.Trim();
        }

        private static string RemoveTagToken(string rawName, string tag)
        {
            int index = rawName.IndexOf('@');
            while (index >= 0)
            {
                int end = index + 1;
                while (end < rawName.Length && IsTagChar(rawName[end]))
                {
                    end++;
                }

                if (end > index + 1 &&
                    string.Equals(rawName.Substring(index, end - index), tag, StringComparison.OrdinalIgnoreCase))
                {
                    return rawName.Remove(index, end - index);
                }

                index = rawName.IndexOf('@', index + 1);
            }

            return rawName;
        }

        private static bool IsTagChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }
    }
}
