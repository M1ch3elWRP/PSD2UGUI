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

        // å›¾ç‰‡ä¹å®«æ ¼
        public bool hasSlice;
        public Vector4 sliceBorder; // (left, bottom, right, top)

        public string layoutType; // "Horizontal", "Vertical", "Grid", "None"

        // 解析后的辅助属性
        public string cleanName; // 去除后缀后的名字 (用于Unity节点命名)
        public string uiType;    // 类型: "Button", "Image", "RawImage", "Text"
    }

    [Serializable]
    public class PSDData
    {
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
            psdData.psdAssetsFolder = Path.GetDirectoryName(jsonPath);

            string jsonText = File.ReadAllText(jsonPath);
            jsonText = NormalizePsDataFormat(jsonText);
            var jsonJo = JObject.Parse(jsonText);

            JObject jopxdata = (JObject)jsonJo["canvas"];
            psdData.width = (int)jopxdata["width"];
            psdData.height = (int)jopxdata["height"];

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
            return psdData;
        }

        private static PicData ParsePicData(string groupName, JObject jodata, int canvasHeight)
        {
            var data = new PicData();
            data.groupName = groupName;
            data.pngName = ((string)jodata["pngname"]).Trim(); // trim: 防 JSX 端残留前/后空白导致文件名不匹配

            // 解析 ID (确保 JS 脚本导出了 id 字段)
            if (jodata["id"] != null) data.id = (int)jodata["id"];
            else data.id = StableHash32(data.pngName); // 兜底：稳定哈希

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

            // 默认值初始化
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
                    case "Item":
                        data.uiType = "Item";
                        break;
                    case "Image":
                        // 显式标记为 Image 或 Bg 的
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
                                        jsonUiType == "Item";
                }
                else
                {
                    usesPrecalcBounds =
                        rawName.IndexOf("@H", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        rawName.IndexOf("@V", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        rawName.IndexOf("@G", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        rawName.IndexOf("@Item", StringComparison.OrdinalIgnoreCase) >= 0;
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

            if (rawName.EndsWith("@H", StringComparison.OrdinalIgnoreCase) || rawName.EndsWith("@HLayout", StringComparison.OrdinalIgnoreCase))
            {
                data.layoutType = "Horizontal";
                data.cleanName = rawName.Replace("@HLayout", "").Replace("@H", ""); // 清理名字
            }
            else if (rawName.EndsWith("@V", StringComparison.OrdinalIgnoreCase) || rawName.EndsWith("@VLayout", StringComparison.OrdinalIgnoreCase))
            {
                data.layoutType = "Vertical";
                data.cleanName = rawName.Replace("@VLayout", "").Replace("@V", "");
            }
            else if (rawName.EndsWith("@G", StringComparison.OrdinalIgnoreCase) || rawName.EndsWith("@Grid", StringComparison.OrdinalIgnoreCase))
            {
                data.layoutType = "Grid";
                data.cleanName = rawName.Replace("@Grid", "").Replace("@G", "");
            }

            if (rawName.EndsWith("@Btn", StringComparison.OrdinalIgnoreCase))
            {
                data.uiType = "Button";
                data.cleanName = rawName.Replace("@Btn", "").Replace("@btn", "");
            }
            else if (rawName.EndsWith("@ImgNoTrim", StringComparison.OrdinalIgnoreCase))
            {
                data.uiType = "Image";
                data.cleanName = rawName.Replace("@ImgNoTrim", "").Replace("@imgnotrim", "");
            }
            else if (rawName.EndsWith("@Bg", StringComparison.OrdinalIgnoreCase))
            {
                data.uiType = "RawImage";
                data.cleanName = rawName.Replace("@Bg", "").Replace("@bg", "");
            }
            else if (rawName.EndsWith("@Image", StringComparison.OrdinalIgnoreCase))
            {
                data.uiType = "Image";
                data.cleanName = rawName.Replace("@Image", "").Replace("@image", "");
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

            var converted = new JObject
            {
                ["pngname"] = asset["pngName"] ?? asset["name"] ?? "",
                ["id"] = sourceNodeId,
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
            // Pass through text-layer fields if present in the asset record
            if (converted["isText"].ToObject<bool>())
            {
                converted["content"] = asset["textContent"] ?? "";
                converted["fontSize"] = asset["fontSize"];
                converted["fontColor"] = asset["fontColor"] ?? "#000000";
            }
            return ParsePicData(groupName, converted, canvasHeight);
        }

        private static void ParseSkeleton(PSDData psdData, JArray skeletonArray)
        {
            if (skeletonArray == null) return;

            for (int i = 0; i < skeletonArray.Count; i++)
            {
                var jo = skeletonArray[i] as JObject;
                if (jo == null) continue;
                var n = new PsdSkeletonNode
                {
                    nodeId = (int?)jo["nodeId"] ?? 0,
                    parentNodeId = (int?)jo["parentNodeId"] ?? 0,
                    hasParent = jo["parentNodeId"] != null,
                    name = (string)jo["name"] ?? string.Empty,
                    rawLayerName = (string)jo["rawLayerName"] ?? string.Empty,
                    sourcePath = (string)jo["sourcePath"] ?? string.Empty,
                    depth = (int?)jo["depth"] ?? 0,
                    siblingIndex = (int?)jo["siblingIndex"] ?? 0,
                    isGroup = (bool?)jo["isGroup"] ?? false,
                    isStructureOnly = (bool?)jo["isStructureOnly"] ?? false,
                    uiTypeHint = (string)jo["uiTypeHint"] ?? "Normal",
                    layoutHint = (string)jo["layoutHint"] ?? "None",
                    exportAssetRef = (string)jo["exportAssetRef"] ?? string.Empty,
                    x = (float?)jo["x"] ?? (float?)jo["absBounds"]?["x"] ?? 0f,
                    y = (float?)jo["y"] ?? (float?)jo["absBounds"]?["y"] ?? 0f,
                    width = (float?)jo["width"] ?? (float?)jo["absBounds"]?["width"] ?? 0f,
                    height = (float?)jo["height"] ?? (float?)jo["absBounds"]?["height"] ?? 0f
                };
                psdData.skeleton.Add(n);
            }
        }

        private static Dictionary<int, string> BuildGroupNameMapFromSkeleton(JArray skeletonArray)
        {
            var map = new Dictionary<int, string>();
            if (skeletonArray == null) return map;

            var parentByNode = new Dictionary<int, int?>();
            var nameByNode = new Dictionary<int, string>();
            for (int i = 0; i < skeletonArray.Count; i++)
            {
                var jo = skeletonArray[i] as JObject;
                if (jo == null) continue;
                int nodeId = (int?)jo["nodeId"] ?? 0;
                if (nodeId == 0) continue;
                parentByNode[nodeId] = (int?)jo["parentNodeId"];
                nameByNode[nodeId] = (string)jo["name"] ?? (string)jo["rawLayerName"] ?? string.Empty;
            }

            foreach (var kv in parentByNode)
            {
                int nodeId = kv.Key;
                var segs = new List<string>();
                int? cursor = kv.Value;
                int guard = 0;
                while (cursor.HasValue && guard < 2048)
                {
                    guard++;
                    if (!nameByNode.TryGetValue(cursor.Value, out string parentName)) break;
                    if (!string.IsNullOrEmpty(parentName)) segs.Add(parentName);
                    if (!parentByNode.TryGetValue(cursor.Value, out int? nextParent)) break;
                    cursor = nextParent;
                }
                segs.Reverse();
                map[nodeId] = segs.Count > 0 ? string.Join("/", segs) + "/" : "root/";
            }

            return map;
        }

        private static string GroupNameFromSourcePath(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath)) return "root/";
            // trim: 防 JSX 端残留前后空白（旧 .ps.data 数据兼容）
            string trimmed = sourcePath.Trim();
            int idx = trimmed.LastIndexOf('/');
            if (idx <= 0) return "root/";
            return trimmed.Substring(0, idx).Trim() + "/";
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
