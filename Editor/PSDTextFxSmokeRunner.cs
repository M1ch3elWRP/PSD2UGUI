using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TZ.UI;

namespace PSDImporter
{
    internal static class PSDTextFxSmokeRunner
    {
        private const string TestAssetPath = "Assets/_OpenCode/TZUI/PS/520/情人节520-NPC情侣配对头图.ps.data";

        [MenuItem("PSDTools/Debug/Run Text FX Smoke", priority = 201)]
        private static void Run()
        {
            PSDData psdData = PSDLoader.ReadJson(TestAssetPath);
            if (psdData == null)
            {
                Debug.LogError("[TextFxSmoke] Failed to read test asset: " + TestAssetPath);
                return;
            }

            PSDImportConfig config = PSDImportWorkflow.FindDefaultConfigAsset();
            RectTransform rootRect = PSDCreateor.CreateUGUI_GenerateMode(psdData, config);
            GameObject root = rootRect != null ? rootRect.gameObject : null;
            if (root == null)
            {
                Debug.LogError("[TextFxSmoke] Create returned null root.");
                return;
            }

            Selection.activeGameObject = root;

            List<PicData> fxItems = psdData.listPngData
                .Where(x => x.isText && (x.hasStroke || x.hasGradient))
                .ToList();

            Text[] allTexts = root.GetComponentsInChildren<Text>(true);
            Debug.Log($"[TextFxSmoke] root={root.name} expectedFxTexts={fxItems.Count} generatedTexts={allTexts.Length}");

            foreach (PicData item in fxItems)
            {
                Text matched = FindBestTextMatch(allTexts, item);
                UITextOutline outline = matched != null ? matched.GetComponent<UITextOutline>() : null;
                UITextGradient gradient = matched != null ? matched.GetComponent<UITextGradient>() : null;
                string nodePath = matched != null ? GetTransformPath(matched.transform) : "<missing>";
                string componentType = matched != null ? matched.GetType().Name : "<none>";

                Debug.Log(
                    $"[TextFxSmoke] PSD='{item.cleanName}' text='{item.textContent}' hasStroke={item.hasStroke} hasGradient={item.hasGradient} " +
                    $"-> node='{nodePath}' component={componentType} outline={(outline != null)} gradient={(gradient != null)}");
            }

            UIStdButton[] stdButtons = root.GetComponentsInChildren<UIStdButton>(true);
            foreach (UIStdButton button in stdButtons)
            {
                string title = button.title != null ? button.title.text : "<null>";
                Debug.Log($"[TextFxSmoke] StdButton node='{GetTransformPath(button.transform)}' state={button.ButtonState} title='{title}' leftText='{button.leftText}'");
            }
        }

        private static Text FindBestTextMatch(IEnumerable<Text> texts, PicData item)
        {
            if (texts == null)
            {
                return null;
            }

            string cleanName = Normalize(item.cleanName);
            string pngName = Normalize(item.pngName);
            string textContent = (item.textContent ?? string.Empty).Trim();

            Text exactName = texts.FirstOrDefault(t =>
                string.Equals(Normalize(t.gameObject.name), cleanName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Normalize(t.gameObject.name), pngName, StringComparison.OrdinalIgnoreCase));
            if (exactName != null)
            {
                return exactName;
            }

            Text exactText = texts.FirstOrDefault(t =>
                string.Equals((t.text ?? string.Empty).Trim(), textContent, StringComparison.OrdinalIgnoreCase));
            if (exactText != null)
            {
                return exactText;
            }

            return texts.FirstOrDefault();
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Trim();
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            Stack<string> names = new Stack<string>();
            Transform current = transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", names.ToArray());
        }
    }
}
