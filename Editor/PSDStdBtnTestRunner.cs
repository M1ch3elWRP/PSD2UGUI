using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using TZ.UI;

namespace PSDImporter
{
    internal static class PSDStdBtnTestRunner
    {
        private const string TestAssetPath = "Assets/_OpenCode/TZUI/PS/StdBtnTest/未标题-1.ps.data";

        [MenuItem("PSDTools/Debug/Run StdBtnTest Create Smoke", priority = 200)]
        private static void RunCreateSmoke()
        {
            PSDData psdData = PSDLoader.ReadJson(TestAssetPath);
            if (psdData == null)
            {
                Debug.LogError("[StdBtnTest] Failed to read test asset: " + TestAssetPath);
                return;
            }

            PSDImportConfig config = PSDImportWorkflow.FindDefaultConfigAsset();
            bool migrated = PrepareLegacyStdBtnData(psdData);

            RectTransform rootRect = PSDCreateor.CreateUGUI_GenerateMode(psdData, config);
            GameObject root = rootRect != null ? rootRect.gameObject : null;
            if (root == null)
            {
                Debug.LogError("[StdBtnTest] Create returned null root.");
                return;
            }

            Selection.activeGameObject = root;

            UIStdButton[] stdButtons = root.GetComponentsInChildren<UIStdButton>(true);
            Debug.Log($"[StdBtnTest] Create success. root={root.name} buttons={stdButtons.Length} migrated={migrated} layers={psdData.listPngData.Count}");
            for (int i = 0; i < stdButtons.Length; i++)
            {
                UIStdButton button = stdButtons[i];
                string title = button.title != null ? button.title.text : "<null>";
                RectTransform buttonRt = button.transform as RectTransform;
                PicData matchedItem = psdData.listPngData.FirstOrDefault(x =>
                    PSDStdPrefabSupport.IsStdButton(x) &&
                    string.Equals(PSDStdPrefabSupport.StripStdPrefabTags(x.pngName), button.name, System.StringComparison.OrdinalIgnoreCase));
                string expected = matchedItem.id != 0
                    ? $"expectedCenter=({matchedItem.x:F1},{matchedItem.y:F1})"
                    : "expectedCenter=<missing>";
                string actual = buttonRt != null
                    ? $"actualLocal=({buttonRt.localPosition.x:F1},{buttonRt.localPosition.y:F1}) actualWorld=({buttonRt.position.x:F1},{buttonRt.position.y:F1})"
                    : "actual=<no-rt>";
                Debug.Log($"[StdBtnTest] Btn[{i}] name={button.name} state={button.ButtonState} title={title} leftText={button.leftText} {expected} {actual}");
            }

            if (!stdButtons.Any())
            {
                Debug.LogWarning("[StdBtnTest] No UIStdButton found under generated root.");
            }
        }

        private static bool PrepareLegacyStdBtnData(PSDData psdData)
        {
            if (psdData?.listPngData == null || psdData.listPngData.Count == 0)
            {
                return false;
            }

            Dictionary<int, int> parentByNodeId = new Dictionary<int, int>();
            HashSet<int> stdRootIds = new HashSet<int>();

            for (int i = 0; i < psdData.listPngData.Count; i++)
            {
                PicData item = psdData.listPngData[i];
                if (item.id != 0)
                {
                    parentByNodeId[item.id] = item.parentNodeId;
                }

                if (PSDStdPrefabSupport.IsStdPrefabRoot(item) && item.id != 0)
                {
                    stdRootIds.Add(item.id);
                }
            }

            if (psdData.skeleton != null)
            {
                for (int i = 0; i < psdData.skeleton.Count; i++)
                {
                    PsdSkeletonNode node = psdData.skeleton[i];
                    if (node.nodeId != 0)
                    {
                        parentByNodeId[node.nodeId] = node.parentNodeId;
                    }
                }
            }

            bool changed = false;
            for (int i = 0; i < psdData.listPngData.Count; i++)
            {
                PicData rootItem = psdData.listPngData[i];
                if (!PSDStdPrefabSupport.IsStdPrefabRoot(rootItem))
                {
                    continue;
                }

                if (rootItem.stdTextItems != null && rootItem.stdTextItems.Count > 0)
                {
                    continue;
                }

                List<StdPrefabTextData> texts = psdData.listPngData
                    .Where(x => x.id != 0 && x.isText && FindStdRootId(x.parentNodeId, parentByNodeId, stdRootIds) == rootItem.id)
                    .OrderBy(x => x.x)
                    .ThenByDescending(x => x.y)
                    .Select(x => ConvertLegacyStdText(rootItem, x))
                    .ToList();

                if (texts.Count == 0)
                {
                    continue;
                }

                rootItem.stdTextItems = texts;
                psdData.listPngData[i] = rootItem;
                changed = true;
            }

            if (!changed)
            {
                return false;
            }

            psdData.listPngData = psdData.listPngData
                .Where(x => x.id == 0 || x.isStdPrefabRoot || FindStdRootId(x.parentNodeId, parentByNodeId, stdRootIds) == 0)
                .ToList();

            return true;
        }

        private static StdPrefabTextData ConvertLegacyStdText(PicData rootItem, PicData textItem)
        {
            float rootLeft = rootItem.x - rootItem.width * 0.5f;
            float rootTop = rootItem.y + rootItem.height * 0.5f;
            float localCenterX = textItem.x - rootLeft;
            float localCenterY = rootTop - textItem.y;

            return new StdPrefabTextData
            {
                sourceNodeId = textItem.id,
                name = !string.IsNullOrEmpty(textItem.cleanName) ? textItem.cleanName : textItem.pngName,
                textContent = textItem.textContent,
                fontSize = textItem.fontSize,
                fontColor = textItem.fontColor,
                textOpacity = textItem.textOpacity,
                lineSpacing = textItem.lineSpacing,
                textAlign = textItem.textAlign,
                hasStroke = textItem.hasStroke,
                strokeColor = textItem.strokeColor,
                strokeSize = textItem.strokeSize,
                width = textItem.width,
                height = textItem.height,
                localCenterX = localCenterX,
                localCenterY = localCenterY,
                normalizedCenterX = rootItem.width > 0.01f ? Mathf.Clamp01(localCenterX / rootItem.width) : 0.5f,
                normalizedCenterY = rootItem.height > 0.01f ? Mathf.Clamp01(localCenterY / rootItem.height) : 0.5f
            };
        }

        private static int FindStdRootId(int parentNodeId, Dictionary<int, int> parentByNodeId, HashSet<int> stdRootIds)
        {
            int guard = 0;
            int current = parentNodeId;
            while (current > 0 && guard++ < 1024)
            {
                if (stdRootIds.Contains(current))
                {
                    return current;
                }

                if (!parentByNodeId.TryGetValue(current, out current))
                {
                    break;
                }
            }

            return 0;
        }
    }
}
