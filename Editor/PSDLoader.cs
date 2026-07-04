using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace PSDImporter
{
    // ---------------------------------------------------------
    // 数据结构定义
    // ---------------------------------------------------------

    [Serializable]
    public struct PicData
    {
        public string groupName;
        public string pngName;  // 原始 PSD 图层名
        public int id;          // 【新增】PS图层唯一ID (持久化绑定的关键)
        public int index;

        public float x;
        public float y;
        public float width;
        public float height;

        // 文本属性
        public bool isText;
        public string textContent;
        public float fontSize;
        public Color fontColor;
        public float textOpacity;   // PS图层不透明度 0~100
        public float lineSpacing;   // 行间距(px)，-1表示未设置（使用默认）
        public string textAlign;    // 对齐方式: "left", "center", "right"

        // 文本描边 (frameFX)
        public bool hasStroke;
        public Color strokeColor;
        public float strokeSize;     // 描边宽度(px)

        // 文本渐变 (gradientFill)
        public bool hasGradient;
        public Color gradientTopColor;
        public Color gradientBottomColor;
        public bool gradientVertical; // true=上下渐变，false=左右渐变

        // å›¾ç‰‡ä¹å®«æ ¼
        public bool hasSlice;
        public Vector4 sliceBorder; // (left, bottom, right, top)

        public string layoutType; // "Horizontal", "Vertical", "Grid", "None"

        // 解析后的辅助属性
        public string cleanName; // 去除后缀后的名字 (用于Unity节点命名)
        public string uiType;    // 类型: "Button", "Image", "Text"

        // 父子关系（用于 Restore 匹配时的父级亲和力评分）
        public bool hasParent;
        public int parentNodeId;
        public bool excludeFromRestore;
        public bool isScrollContentAlias;
        public int scrollRectRootId;
    }

    [Serializable]
    public class PSDData
    {
        public string templatePngPath;
        public bool hasTemplatePng;
        public string psdAssetsFolder; // 图片资源所在文件夹
        public int width;              // 画布宽
        public int height;             // 画布高
        public List<PicData> listPngData = new List<PicData>();
        public List<PsdSkeletonNode> skeleton = new List<PsdSkeletonNode>();
        public bool HasSkeleton => skeleton != null && skeleton.Count > 0;
    }

    [Serializable]
    public class PsdSkeletonNode
    {
        public int nodeId;
        public int parentNodeId;
        public bool hasParent;
        public string name;
        public string rawLayerName;
        public string sourcePath;
        public int depth;
        public int siblingIndex;
        public bool isGroup;
        public bool isStructureOnly;
        public string uiTypeHint;
        public string layoutHint;
        /// <summary>
        /// JSX端标记：此节点是否有对应的导出资源(PNG/Text)。
        /// 空的字符串=无导出资源，非空=有导出资源（如 "asset_001"）。
        /// 比isStructureOnly更靠谱——因为ArtLayer只要有尺寸就会被标hasVisualOutput=true，
        /// 但实际可能没有导出PNG（如纯形状残留层）。
        /// </summary>
        public string exportAssetRef;
        public float x;
        public float y;
        public float width;
        public float height;
    }

    // ---------------------------------------------------------
    // 读取工具类
    // ---------------------------------------------------------
    public class PSDLoader
    {
        const string exname = ".ps.data";

        /// <summary>
        /// Normalize .ps.data content to valid JSON.
        /// Some ExportToPNG.jsx versions output JavaScript object literal format
        /// (paren-wrapped, unquoted keys) instead of strict JSON.
        /// This method detects and fixes both issues so JObject.Parse can handle it.
        /// </summary>
        private static string NormalizePsDataFormat(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Strip outer parentheses if present: ({...}) → {...}
            string trimmed = text.TrimStart();
            if (trimmed.StartsWith("("))
            {
                trimmed = trimmed.Substring(1);
                // Also strip trailing ) if present
                if (trimmed.EndsWith(")"))
                    trimmed = trimmed.Substring(0, trimmed.Length - 1);
                text = trimmed;
            }

            // If first non-whitespace char is {, it's likely JS object literal with unquoted keys.
            // Use regex to add double-quotes around unquoted object keys.
            // This handles keys like: meta:{...}, assets:[...], "canvas":{width:2212,...}
            // Pattern matches word characters followed by colon at key positions
            string jsonCandidate = text.TrimStart();
            if ((jsonCandidate.StartsWith("{") || jsonCandidate.StartsWith("[")) &&
                !jsonCandidate.StartsWith("\""))
            {
                // Quote unquoted keys: match identifiers before ':' that are not already quoted
                text = System.Text.RegularExpressions.Regex.Replace(
                    text,
                    @"(?<=[{\[,])\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*:",
                    "\"$1\":",
                    System.Text.RegularExpressions.RegexOptions.Compiled);
            }

            return text;
        }

        public static PSDData ReadJson(string jsonPath)
        {
            if (!jsonPath.EndsWith(exname)) return null;

            PSDData psdData = new PSDData();
            psdData.psdAssetsFolder = (Path.GetDirectoryName(jsonPath) ?? string.Empty).Replace("\\", "/");

            string jsonText = File.ReadAllText(jsonPath);
            jsonText = NormalizePsDataFormat(jsonText);
            var jsonJo = JObject.Parse(jsonText);
            JObject meta = jsonJo["meta"] as JObject;

            JObject jopxdata = (JObject)jsonJo["canvas"];
            psdData.width = (int)jopxdata["width"];
            psdData.height = (int)jopxdata["height"];
            psdData.templatePngPath = ResolveTemplatePngPath(psdData.psdAssetsFolder, meta);
            psdData.hasTemplatePng = !string.IsNullOrEmpty(psdData.templatePngPath);

            JObject jp = jsonJo["pngdata"] as JObject;
            JArray assets = jsonJo["assets"] as JArray;
            JArray skeleton = jsonJo["skeleton"] as JArray;

            Dictionary<int, string> groupNameByNodeId = BuildGroupNameMapFromSkeleton(skeleton);
            ParseSkeleton(psdData, skeleton);

            if (assets != null && assets.Count > 0)
            {
                for (int i = 0; i < assets.Count; i++)
                {
                    JObject asset = assets[i] as JObject;
                    if (asset == null) continue;
                    var data = ParsePicDataFromAsset(asset, psdData.height, i, groupNameByNodeId);
                    psdData.listPngData.Add(data);
                }
            }
            else if (jp != null)
            {
                foreach (JProperty group in jp.Children())
                {
                    foreach (var pngdata in group.Value)
                    {
                        JObject jodata = (JObject)pngdata;
                        var data = ParsePicData(group.Name, jodata, psdData.height);
                        psdData.listPngData.Add(data);
                    }
                }
            }

            // 排序 (Index倒序 -> 渲染顺序正序)
            psdData.listPngData.Sort((a, b) => a.index.CompareTo(b.index));
            PostProcessScrollRectFlags(psdData);
            return psdData;
        }

        private static string ResolveTemplatePngPath(string psdAssetsFolder, JObject meta)
        {
            string templatePath = null;
            if (meta != null)
            {
                templatePath = (string)meta["templatePngPath"] ?? (string)meta["templatePng"];
            }

            string resolved = ResolveTemplateCandidate(psdAssetsFolder, templatePath);
            if (!string.IsNullOrEmpty(resolved))
            {
                return resolved;
            }

            return ResolveTemplateCandidate(psdAssetsFolder, "template.png");
        }

        private static string ResolveTemplateCandidate(string psdAssetsFolder, string templatePath)
        {
            if (string.IsNullOrEmpty(templatePath))
            {
                return string.Empty;
            }

            string normalized = templatePath.Replace("\\", "/");
            string candidate = Path.IsPathRooted(normalized)
                ? normalized
                : $"{(psdAssetsFolder ?? string.Empty).TrimEnd('/', '\\')}/{normalized}";
            candidate = NormalizeUnityAssetPath(candidate);
            return AssetOrAbsoluteFileExists(candidate) ? candidate : string.Empty;
        }

        private static bool AssetOrAbsoluteFileExists(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            if (File.Exists(path))
            {
                return true;
            }

            string normalized = path.Replace("\\", "/");
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace("\\", "/");
                string absolute = Path.Combine(projectRoot, normalized).Replace("\\", "/");
                return File.Exists(absolute);
            }

            return false;
        }

        private static string NormalizeUnityAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            string normalized = path.Replace("\\", "/");
            int assetsIndex = normalized.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
            return assetsIndex >= 0 ? normalized.Substring(assetsIndex) : normalized;
        }

        private static PicData ParsePicData(string groupName, JObject jodata, int canvasHeight)
        {
            var data = new PicData();
            data.groupName = groupName;
            data.pngName = ((string)jodata["pngname"]).Trim(); // trim: 防 JSX 端残留前/后空白导致文件名不匹配

            // 解析 ID (确保 JS 脚本导出了 id 字段)
            if (jodata["id"] != null) data.id = (int)jodata["id"];
            else data.id = StableHash32(data.pngName); // 兜底：稳定哈希

            // 解析父子关系
            data.parentNodeId = (int?)jodata["parentNodeId"] ?? 0;
            data.hasParent = data.parentNodeId > 0;
            data.excludeFromRestore = false;
            data.isScrollContentAlias = false;
            data.scrollRectRootId = 0;

            data.index = (int)jodata["index"];
            data.x = (float)jodata["x"];
            data.y = (float)jodata["y"];
            data.width = (float)jodata["width"];
            data.height = (float)jodata["height"];

            data.hasSlice = false;
            data.sliceBorder = Vector4.zero;
            if (jodata["slice"] != null)
            {
                var sliceArr = jodata["slice"] as JArray;
                if (sliceArr != null && sliceArr.Count >= 4)
                {
                    float left = (float)sliceArr[0];
                    float top = (float)sliceArr[1];
                    float right = (float)sliceArr[2];
                    float bottom = (float)sliceArr[3];
                    if (left > 0 || right > 0 || top > 0 || bottom > 0)
                    {
                        data.hasSlice = true;
                        data.sliceBorder = new Vector4(left, bottom, right, top);
                    }
                }
            }

            // --- 核心修改：读取新的 uiType 字段 ---
            string jsonUiType = (string)jodata["uiType"]; // 读取 JSX 写入的类型

            data.uiType = "Image";
            data.layoutType = "None";

            // 文本解析
            data.isText = jodata["isText"] != null && (bool)jodata["isText"];
            if (data.isText)
            {
                data.textContent = (string)jodata["content"];
                data.fontSize = (float)jodata["fontSize"];
                string hexColor = (string)jodata["fontColor"];
                ColorUtility.TryParseHtmlString(hexColor, out data.fontColor);
                data.textOpacity = jodata["opacity"] != null ? (float)jodata["opacity"] : 100f;
                data.lineSpacing = jodata["lineSpacing"] != null ? (float)jodata["lineSpacing"] : -1f;
                data.textAlign = jodata["textAlign"] != null ? (string)jodata["textAlign"] : "center";

                // 描边
                data.hasStroke = jodata["hasStroke"] != null && (bool)jodata["hasStroke"];
                if (data.hasStroke)
                {
                    ColorUtility.TryParseHtmlString((string)jodata["strokeColor"], out data.strokeColor);
                    data.strokeSize = jodata["strokeSize"] != null ? (float)jodata["strokeSize"] : 0f;
                }

                // 渐变
                data.hasGradient = jodata["hasGradient"] != null && (bool)jodata["hasGradient"];
                if (data.hasGradient)
                {
                    ColorUtility.TryParseHtmlString((string)jodata["gradientTopColor"], out data.gradientTopColor);
                    ColorUtility.TryParseHtmlString((string)jodata["gradientBottomColor"], out data.gradientBottomColor);
                    data.gradientVertical = jodata["gradientVertical"] != null ? (bool)jodata["gradientVertical"] : true;
                }

                data.uiType = "Text";
            }
            else
            {
                // 根据 JSX 的 uiType 映射到 C# 的逻辑
                switch (jsonUiType)
                {
                    case "Button":
                        data.uiType = "Button";
                        break;
                    case "Horizontal":
                        data.layoutType = "Horizontal";
                        data.uiType = "Layout"; // 标记为 Layout 容器
                        break;
                    case "Vertical":
                        data.layoutType = "Vertical";
                        data.uiType = "Layout";
                        break;
                    case "Grid":
                        data.layoutType = "Grid";
                        data.uiType = "Layout";
                        break;
                    case "ScrollRect":
                        data.uiType = "ScrollRect";
                        break;
                    case "Item":
                        data.uiType = "Item";
                        break;
                    case "Image":
                        // 显式标记为 Image 的
                        data.uiType = "Image";
                        break;
                    case "Normal":
                    default:
                        // 普通图层默认为 Image
                        data.uiType = "Image";
                        break;
                }
            }

            // 后缀解析与清洗
            string rawName = data.pngName;
            data.cleanName = rawName;

            // JSX 预计算的容器坐标默认是左上原点；若标记为 bottom-left 则无需翻转
            bool hasPrecalcFlag = jodata["precalc"] != null && (bool)jodata["precalc"];
            string precalcOrigin = (string)jodata["precalcOrigin"];

            bool usesPrecalcBounds = hasPrecalcFlag;
            if (!usesPrecalcBounds)
            {
                if (!string.IsNullOrEmpty(jsonUiType))
                {
                    usesPrecalcBounds = jsonUiType == "Horizontal" ||
                                        jsonUiType == "Vertical" ||
                                        jsonUiType == "Grid" ||
                                        jsonUiType == "ScrollRect" ||
                                        jsonUiType == "Item";
                }
                else
                {
                    usesPrecalcBounds =
                        PSDTagUtility.HasAnyTag(rawName, "@ScrollRect", "@H", "@HLayout", "@V", "@VLayout", "@G", "@Grid", "@Item");
                }
            }

            bool shouldFlipPrecalcY = false;
            if (usesPrecalcBounds)
            {
                if (!string.IsNullOrEmpty(precalcOrigin))
                {
                    shouldFlipPrecalcY = !string.Equals(precalcOrigin, "bottom-left", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    shouldFlipPrecalcY = true;
                }
            }

            if (shouldFlipPrecalcY && canvasHeight > 0)
            {
                data.y = canvasHeight - data.y;
            }

            if (PSDTagUtility.HasAnyTag(rawName, "@H", "@HLayout"))
            {
                data.layoutType = "Horizontal";
                data.cleanName = PSDTagUtility.RemoveTags(rawName, "@HLayout", "@H"); // 清理名字
            }
            else if (PSDTagUtility.HasAnyTag(rawName, "@V", "@VLayout"))
            {
                data.layoutType = "Vertical";
                data.cleanName = PSDTagUtility.RemoveTags(rawName, "@VLayout", "@V");
            }
            else if (PSDTagUtility.HasAnyTag(rawName, "@G", "@Grid"))
            {
                data.layoutType = "Grid";
                data.cleanName = PSDTagUtility.RemoveTags(rawName, "@Grid", "@G");
            }

            if (PSDTagUtility.HasTag(rawName, "@ScrollRect"))
            {
                data.uiType = "ScrollRect";
                data.cleanName = PSDTagUtility.RemoveTags(rawName, "@ScrollRect", "@HLayout", "@H", "@VLayout", "@V", "@Grid", "@G");
            }
            else if (PSDTagUtility.HasTag(rawName, "@Btn"))
            {
                data.uiType = "Button";
                data.cleanName = PSDTagUtility.RemoveTags(rawName, "@Btn");
            }
            else if (PSDTagUtility.HasTag(rawName, "@Item"))
            {
                data.uiType = "Item";
                data.cleanName = PSDTagUtility.RemoveTags(rawName, "@Item");
            }
            else if (PSDTagUtility.HasTag(rawName, "@ImgNoTrim"))
            {
                data.uiType = "Image";
                data.cleanName = PSDTagUtility.RemoveTags(rawName, "@ImgNoTrim", "@Bg");
            }
            else if (PSDTagUtility.HasAnyTag(rawName, "@Img", "@Image"))
            {
                data.uiType = "Image";
                data.cleanName = PSDTagUtility.RemoveTags(rawName, "@Img", "@Image", "@Bg");
            }

            return data;
        }

        private static PicData ParsePicDataFromAsset(JObject asset, int canvasHeight, int fallbackIndex, Dictionary<int, string> groupNameByNodeId)
        {
            int sourceNodeId = (int?)asset["sourceNodeId"] ?? 0;
            string groupName = "root/";
            if (sourceNodeId != 0 && groupNameByNodeId != null && groupNameByNodeId.TryGetValue(sourceNodeId, out string mappedGroup))
            {
                groupName = mappedGroup;
            }
            else
            {
                groupName = GroupNameFromSourcePath((string)asset["sourcePath"]);
            }

            string assetUiType = (string)asset["uiType"] ?? "Normal";
            bool inferredPrecalc = assetUiType == "Horizontal" ||
                                   assetUiType == "Vertical" ||
                                   assetUiType == "Grid" ||
                                   assetUiType == "ScrollRect" ||
                                   assetUiType == "Item";

            var converted = new JObject
            {
                ["pngname"] = asset["pngName"] ?? asset["name"] ?? "",
                ["id"] = sourceNodeId,
                ["parentNodeId"] = asset["parentNodeId"] ?? 0,
                ["index"] = fallbackIndex,
                ["x"] = asset["trimBounds"]?["x"] != null && asset["trimBounds"]?["width"] != null
                    ? (float)asset["trimBounds"]["x"] + ((float?)asset["trimBounds"]["width"] ?? 0f) * 0.5f
                    : (float?)asset["absBounds"]?["x"] ?? 0f,
                ["y"] = asset["trimBounds"]?["y"] != null && asset["trimBounds"]?["height"] != null
                    ? (float)asset["trimBounds"]["y"] + ((float?)asset["trimBounds"]["height"] ?? 0f) * 0.5f
                    : (float?)asset["absBounds"]?["y"] ?? 0f,
                ["width"] = (float?)asset["trimBounds"]?["width"] ?? (float?)asset["absBounds"]?["width"] ?? 0f,
                ["height"] = (float?)asset["trimBounds"]?["height"] ?? (float?)asset["absBounds"]?["height"] ?? 0f,
                ["uiType"] = asset["uiType"] ?? "Normal",
                // v2 fix: read isText from JSX asset record instead of hardcoding false
                ["isText"] = (bool?)asset["isText"] ?? false
            };
            if (asset["precalc"] != null)
            {
                converted["precalc"] = asset["precalc"];
            }
            else if (inferredPrecalc)
            {
                // assets[] stores trimBounds in bottom-left space already; keep std/layout containers from being flipped again.
                converted["precalc"] = true;
            }

            if (asset["precalcOrigin"] != null)
            {
                converted["precalcOrigin"] = asset["precalcOrigin"];
            }
            else if (inferredPrecalc)
            {
                converted["precalcOrigin"] = "bottom-left";
            }
            // Pass through text-layer fields if present in the asset record
            if (converted["isText"].ToObject<bool>())
            {
                converted["content"] = asset["textContent"] ?? "";
                converted["fontSize"] = asset["fontSize"];
                converted["fontColor"] = asset["fontColor"] ?? "#000000";
                converted["opacity"] = asset["opacity"] != null ? asset["opacity"] : 100;
                converted["lineSpacing"] = asset["lineSpacing"] != null ? asset["lineSpacing"] : -1;
                converted["textAlign"] = asset["textAlign"] != null ? asset["textAlign"] : "center";

                // 描边/渐变字段
                converted["hasStroke"] = (bool?)asset["hasStroke"] ?? false;
                converted["strokeColor"] = asset["strokeColor"] ?? "#000000";
                converted["strokeSize"] = asset["strokeSize"] ?? 0;
                converted["hasGradient"] = (bool?)asset["hasGradient"] ?? false;
                converted["gradientTopColor"] = asset["gradientTopColor"] ?? "#FFFFFF";
                converted["gradientBottomColor"] = asset["gradientBottomColor"] ?? "#000000";
                converted["gradientVertical"] = (bool?)asset["gradientVertical"] ?? true;
            }
            return ParsePicData(groupName, converted, canvasHeight);
        }


        private static void PostProcessScrollRectFlags(PSDData psdData)
        {
            if (psdData == null || psdData.listPngData == null || psdData.listPngData.Count == 0)
            {
                return;
            }

            for (int i = 0; i < psdData.listPngData.Count; i++)
            {
                PicData item = psdData.listPngData[i];
                item.isScrollContentAlias = false;
                item.scrollRectRootId = PSDScrollRectUtility.IsScrollRectRoot(item) ? item.id : 0;
                psdData.listPngData[i] = item;
            }

            for (int i = 0; i < psdData.listPngData.Count; i++)
            {
                PicData root = psdData.listPngData[i];
                if (!PSDScrollRectUtility.IsScrollRectRoot(root) || root.id == 0)
                {
                    continue;
                }

                int aliasIndex = FindScrollContentAliasIndex(psdData, root);
                if (aliasIndex < 0)
                {
                    continue;
                }

                PicData alias = psdData.listPngData[aliasIndex];
                alias.isScrollContentAlias = true;
                alias.scrollRectRootId = root.id;
                alias.excludeFromRestore = true;
                psdData.listPngData[aliasIndex] = alias;
            }
        }

        private static int FindScrollContentAliasIndex(PSDData psdData, PicData scrollRoot)
        {
            List<int> candidates = new List<int>();
            for (int i = 0; i < psdData.listPngData.Count; i++)
            {
                PicData item = psdData.listPngData[i];
                if (item.id == 0 ||
                    item.id == scrollRoot.id ||
                    !item.hasParent ||
                    item.parentNodeId != scrollRoot.id ||
                    !string.Equals(item.uiType, "Layout", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(item.layoutType) ||
                    string.Equals(item.layoutType, "None", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidates.Add(i);
            }

            if (candidates.Count == 0)
            {
                return -1;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                PicData item = psdData.listPngData[candidates[i]];
                string name = (item.cleanName ?? item.pngName ?? string.Empty).Trim();
                if (name.IndexOf("Content", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return candidates[i];
                }
            }

            bool rootHasLayout = !string.IsNullOrEmpty(scrollRoot.layoutType) &&
                                 !string.Equals(scrollRoot.layoutType, "None", StringComparison.OrdinalIgnoreCase);
            return !rootHasLayout && candidates.Count == 1 ? candidates[0] : -1;
        }


        private static string GroupNameFromSourcePath(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath)) return "root/";
            // trim: 防 JSX 端残留前后空白（旧 .ps.data 数据兼容）
            string trimmed = sourcePath.Trim();
            // 按 / 拆分路径段（不含最后一个段，即图层自身）
            string[] parts = trimmed.Split('/');
            if (parts.Length <= 1) return "root/";
            // 从后往前遍历父路径段，遇到包含 @ 的段就截断（与 JSX skinName 对齐）
            var segs = new List<string>();
            for (int i = parts.Length - 2; i >= 0; i--) // -2: 排除最后一个段（图层自身）
            {
                string seg = parts[i].Trim();
                if (seg.IndexOf('@') >= 0) break; // @ 标记的组名截断
                if (!string.IsNullOrEmpty(seg)) segs.Add(seg);
            }
            segs.Reverse();
            return segs.Count > 0 ? string.Join("/", segs) + "/" : "root/";
        }

        private static int StableHash32(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 16777619;
                }
                return (int)hash;
            }
        }

        public static bool IsSelectionPSData()
        {
            var obj = Selection.activeObject;
            if (obj == null) return false;
            return AssetDatabase.GetAssetPath(obj).EndsWith(exname);
        }
    }
}
