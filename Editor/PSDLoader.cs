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
    }

    // ---------------------------------------------------------
    // 读取工具类
    // ---------------------------------------------------------
    public class PSDLoader
    {
        const string exname = ".ps.data";

        public static PSDData ReadJson(string jsonPath)
        {
            if (!jsonPath.EndsWith(exname)) return null;

            PSDData psdData = new PSDData();
            psdData.psdAssetsFolder = Path.GetDirectoryName(jsonPath);

            string jsonText = File.ReadAllText(jsonPath);
            var jsonJo = JObject.Parse(jsonText);

            JObject jopxdata = (JObject)jsonJo["canvas"];
            psdData.width = (int)jopxdata["width"];
            psdData.height = (int)jopxdata["height"];

            JObject jp = (JObject)jsonJo["pngdata"];
            foreach (JProperty group in jp.Children())
            {
                foreach (var pngdata in group.Value)
                {
                    JObject jodata = (JObject)pngdata;
                    var data = ParsePicData(group.Name, jodata, psdData.height);
                    psdData.listPngData.Add(data);

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
            data.pngName = (string)jodata["pngname"];

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
