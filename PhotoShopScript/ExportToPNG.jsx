// ExportToPNG.jsx - v8 Decoupled Strategy
// 策略：双轨制 (Dual Track)
// 1. 图片导出：完全保留原版逻辑 (Trim -> Save -> Undo)，确保图片绝对正确。
// 2. Layout数据：在任何裁剪发生前，通过“预计算”获取组的绝对坐标，存入字典。
// 结果：图片不乱，Layout数据也不为0。

// --- Settings ---
var writePngs = true;
var writeTemplate = false;
var writeJson = true;
var ignoreHiddenLayers = true;
var pngScale = 1;
var groupsAsSkins = false;
var trimWhitespace = true; 
var onlyTagged = true;
// Ignore far tiny outliers when trimming @Img groups
var tinyOutlierArea = 1000;
var tinyOutlierMaxDistance = 100;
var saveDir = "C:/Images/";

// --- IDs ---
const settingsID = stringIDToTypeID("settings");
const writePngsID = stringIDToTypeID("writePngs");
const writeTemplateID = stringIDToTypeID("writeTemplate");
const writeJsonID = stringIDToTypeID("writeJson");
const ignoreHiddenLayersID = stringIDToTypeID("ignoreHiddenLayers");
const groupsAsSkinsID = stringIDToTypeID("groupsAsSkins");
const trimWhitespaceID = stringIDToTypeID("trimWhitespace");
const pngScaleID = stringIDToTypeID("pngScale");
const saveDirID = stringIDToTypeID("saveDir");
const onlyTaggedID = stringIDToTypeID("onlyTagged");

var psdName = "";
var originalDoc;
var exportDoc; 
var visibleMap = {};

try {
    originalDoc = app.activeDocument;
} catch (ignored) {}
var settings, progress;
loadSettings();
showDialog();

function run() {
    // 1. Init
    saveSettings();
    
    if (!endsWith(saveDir, "/")) saveDir += "/";
    new Folder(saveDir).create();

    psdName = originalDoc.name;

    // 2. Safe Duplicate
    exportDoc = originalDoc.duplicate();
    app.activeDocument = exportDoc;

    try {
        deleteDocumentAncestorsMetadata();

        // Template
        if (writeTemplate) {
            if (pngScale != 1) scaleImage();
            var file = new File(saveDir + "template" + ".png");
            if (file.exists) file.remove();
            savePNG(file);
            
            // Re-duplicate if scaled
            if (pngScale != 1) {
                exportDoc.close(SaveOptions.DONOTSAVECHANGES);
                exportDoc = originalDoc.duplicate();
                app.activeDocument = exportDoc;
            }
        }

        if (!writeJson && !writePngs) {
            exportDoc.close(SaveOptions.DONOTSAVECHANGES);
            return;
        }

        // =========================================================
        // 【关键步骤 A】预计算 Layout 包围盒 (解耦逻辑)
        // 在做任何 Trim/Hide 操作前，文档是完整的，此时计算组坐标最准。
        // =========================================================
        var layoutBoundsMap = preCalculateAllGroupBounds(exportDoc);
        var usedPngNames = {}; // per output folder, avoid overwrite on duplicate names

        // =========================================================
        // 【关键步骤 B】常规图层收集
        // =========================================================
        var layers = [];
        collectLayers(exportDoc, layers, onlyTagged);

        var canvasWidth = exportDoc.width.as("px");
        var canvasHeight = exportDoc.height.as("px");
        var legacyPngDataMap = {};
        var assets = [];
        var skeleton = [];
        var skeletonByNodeId = {};
        var exportWarnings = [];
        // 先采集 skeleton，再做 hide/trim，避免某些 Photoshop 版本在隐藏层状态下读取 bounds 报“命令不可用”
        // 兜底：若 skeleton 采集失败，不中断主导出流程（仍导出 pngdata/assets）
        try {
            collectSkeletonNodes(exportDoc, null, 0, "", skeleton, skeletonByNodeId, canvasWidth, canvasHeight, exportWarnings);
        } catch (skeletonErr) {
            skeleton = [];
            skeletonByNodeId = {};
            pushWarning(exportWarnings, "skeleton_collect_failed", skeletonErr);
        }

        // 记录原始可见性 (用于导出组时保留隐藏状态)
        cacheVisibleState(exportDoc);

        // 初始全隐藏 (这是原版逻辑的起点)
        hideAllLayers(exportDoc);

        // Skins 分组
        var skins = { "root": [] };
        for (var i = layers.length - 1; i >= 0; i--) {
            var layer = layers[i];
            var skinName = "root";
            if (groupsAsSkins) {
                var parent = layer.parent;
                var path = [];
                while (parent && parent.typename == "LayerSet" && parent.name != psdName && parent.name != "Adobe Photoshop") {
                    if(parent.name.indexOf("@") != -1) break; 
                    path.unshift(trim(parent.name));
                    parent = parent.parent;
                }
                if (path.length > 0) skinName = path.join("/");
            }
            if (!skins[skinName]) skins[skinName] = [];
            skins[skinName].push(layer);
        }

        // =========================================================
        // 【关键步骤 C】主循环 (保留原版 Trim 逻辑)
        // =========================================================
        var skinIndex = 0;
        var totalSkins = 0;
        for(var k in skins) if(skins.hasOwnProperty(k)) totalSkins++;

        for (var skinName in skins) {
            if (!skins.hasOwnProperty(skinName)) continue;
            var skinLayers = skins[skinName];
            
            var skname = (skinName == "root") ? "root" : skinName;
            skname = skname.replace(/\/+/g, "/"); 
            if(skname == "root") skname = "root/"; else skname += "/";
            if (!legacyPngDataMap[skname]) legacyPngDataMap[skname] = [];

            for (var i = skinLayers.length - 1; i >= 0; i--) {
                var layer = skinLayers[i];
                var sourceLayer = layer;
                // PS 2026 安全检查：确认当前迭代的 layer 引用仍然有效
                // （前一次迭代的 history 回滚可能使引用失效）
                var sourceLayerId = 0;
                var slotName = "";
                var rawName = "";
                var isLayerSet = false;
                try {
                    sourceLayerId = layer.id || 0;
                    slotName = layerName(layer);
                    rawName = layer.name || "";
                    isLayerSet = (layer.typename == "LayerSet");
                } catch (headerErr) {
                    // layer 引用已失效，跳过此图层
                    continue;
                }

                // 类型判断
                var suffixType = "Normal";
                var isContainer = false;
                var useLayerBounds = false;

                if (rawName.indexOf("@ImgNoTrim") != -1) { suffixType = "Image"; useLayerBounds = true; }
                else if (rawName.indexOf("@Img") != -1 || rawName.indexOf("@Bg") != -1) suffixType = "Image";
                else if (rawName.indexOf("@Btn") != -1) { suffixType = "Button"; isContainer = true; }
                else if (rawName.indexOf("@H") != -1) { suffixType = "Horizontal"; isContainer = true; }
                else if (rawName.indexOf("@V") != -1) { suffixType = "Vertical"; isContainer = true; }
                else if (rawName.indexOf("@G") != -1) { suffixType = "Grid"; isContainer = true; }
                else if (rawName.indexOf("@Item") != -1) { suffixType = "Item"; isContainer = true; }

                // --- 坐标逻辑分流 ---
                var x = 0, y = 0, width = 0, height = 0;
                var targetDocW = 0, targetDocH = 0;
                var usedPrecalc = false;

                // 分支 1: 容器/Layout -> 直接从“预计算字典”取值 (不执行 Trim，不影响画布)
                if (isLayerSet && isContainer) {
                    var stored = layoutBoundsMap[layer.id];
                    if (stored) {
                        usedPrecalc = true;
                        width = stored.w * pngScale;
                        height = stored.h * pngScale;
                        x = (stored.x * pngScale) + (width / 2);
                        var canvasH = exportDoc.height.as("px") * pngScale;
                        y = canvasH - (stored.y * pngScale) - (height / 2);
                    }
                    // 注意：这里我们完全不触碰 activeDocument 的状态
                } 
                // 分支 2: 需要导图的层 -> 执行原版 Trim 流程 (或 Full 模式)
                else {
                    // 1. 显示当前
                    makeLayerVisible(layer);
                    
                    // 2. 记状态
                    var preTrimState = exportDoc.activeHistoryState;

                    // 2.1 合并/栅格化（避免组或智能对象导出黑图）
                    var isSmartObjectLayer = (layer.typename == "ArtLayer" && layer.kind == LayerKind.SMARTOBJECT);
                    if (useLayerBounds && !isSmartObjectLayer) {
                        try {
                            exportDoc.activeLayer = layer;
                            executeAction(stringIDToTypeID("newPlacedLayer"), undefined, DialogModes.NO);
                            layer = exportDoc.activeLayer;
                            isSmartObjectLayer = (layer.typename == "ArtLayer" && layer.kind == LayerKind.SMARTOBJECT);
                        } catch (e) {}
                    }
                    var customBounds = null;
                    if (useMergedBounds && layer.typename == "LayerSet") {
                        customBounds = getGroupBoundsIgnoringOutliers(layer);
                    }
                    if (!(useLayerBounds && isSmartObjectLayer)) {
                        layer = prepareLayerForExport(layer, suffixType);
                    }

                    var layerBoundsW = 0;
                    var layerBoundsH = 0;
                    var useMergedBounds = false;
                    if (!useLayerBounds && isLayerSet && suffixType == "Image" && trimWhitespace) {
                        useLayerBounds = true;
                        useMergedBounds = true;
                    }
                    if (useLayerBounds) {
                        var b = customBounds ? customBounds : layer.bounds;
                        var l = b[0].as("px");
                        var t = b[1].as("px");
                        var r = b[2].as("px");
                        var bot = b[3].as("px");
                        var rawW = Math.max(0, r - l);
                        var rawH = Math.max(0, bot - t);
                        layerBoundsW = rawW;
                        layerBoundsH = rawH;
                        width = rawW * pngScale;
                        height = rawH * pngScale;
                        var targetW = padToMultipleOf4(width);
                        var targetH = padToMultipleOf4(height);
                        width = targetW;
                        height = targetH;
                        targetDocW = Math.round(width / pngScale);
                        targetDocH = Math.round(height / pngScale);
                        x = (l * pngScale) + (width / 2);
                        var canvasH2 = exportDoc.height.as("px") * pngScale;
                        y = canvasH2 - (t * pngScale) - (height / 2);
                    } else {
                        // 3. 计算 Trim 坐标
                        x = exportDoc.width.as("px") * pngScale;
                        y = exportDoc.height.as("px") * pngScale;

                        if (trimWhitespace) {
                            if (!layer.isBackgroundLayer) exportDoc.trim(TrimType.TRANSPARENT, false, true, true, false);
                            x -= exportDoc.width.as("px") * pngScale;
                            y -= exportDoc.height.as("px") * pngScale;
                            if (!layer.isBackgroundLayer) exportDoc.trim(TrimType.TRANSPARENT, true, false, false, true);
                        }

                        var unscaledW = exportDoc.width.as("px");
                        var unscaledH = exportDoc.height.as("px");
                        var currentW = unscaledW * pngScale;
                        var currentH = unscaledH * pngScale;
                        var targetW = Math.ceil(currentW / 4) * 4;
                        var targetH = Math.ceil(currentH / 4) * 4;
                        if (targetW != currentW || targetH != currentH) {
                            exportDoc.resizeCanvas(UnitValue(targetW, "px"), UnitValue(targetH, "px"), AnchorPosition.MIDDLECENTER);
                        }

                        width = targetW;
                        height = targetH;
                        x += Math.round(width) / 2;
                        y += Math.round(height) / 2;
                    }

                    // 4. 存图
                    var shouldSave = false;
                    if (writePngs) {
                        if (suffixType == "Image") shouldSave = true;
                        else if (suffixType == "Button") { 
                            // Button 比较特殊，如果是组，我们假设它是容器，不存图；
                            // 除非它包含 @Img。但为了兼容，如果它有像素，存也无妨。
                            // 但你之前的需求是 Button 组不存图。
                            // 这里恢复你的逻辑：容器不存。
                            shouldSave = false; 
                        }
                        else if (!isContainer && layer.kind != LayerKind.TEXT) shouldSave = true;
                    }

                    var attachmentName = slotName;
                    var finalDir = saveDir;
                    if (groupsAsSkins && skinName != "root") {
                        finalDir = saveDir + skinName + "/";
                        new Folder(finalDir).create();
                    }
                    attachmentName = allocateUniquePngName(attachmentName, finalDir, usedPngNames);
                    slotName = attachmentName;

                    if (useLayerBounds && isSmartObjectLayer) {
                        var saveFile = shouldSave ? new File(finalDir + attachmentName + ".png") : null;
                        var trimInfo = exportSmartObjectTrimmed(layer, saveFile);
                        if (trimInfo) {
                            var sx = (layerBoundsW > 0 && trimInfo.soW > 0) ? (layerBoundsW / trimInfo.soW) : 1;
                            var sy = (layerBoundsH > 0 && trimInfo.soH > 0) ? (layerBoundsH / trimInfo.soH) : 1;
                            width = trimInfo.finalW * sx;
                            height = trimInfo.finalH * sy;
                            var dx = ((trimInfo.left - trimInfo.right) * 0.5) * sx * pngScale;
                            var dy = ((trimInfo.top - trimInfo.bottom) * 0.5) * sy * pngScale;
                            x += dx;
                            y -= dy;
                        }
                    } else if (shouldSave) {
                        if (width > 0 && height > 0) {
                            if (useLayerBounds) {
                                var saveFile = new File(finalDir + attachmentName + ".png");
                                if (isSmartObjectLayer) {
                                    exportSmartObjectTrimmed(layer, saveFile);
                                } else {
                                    var tempDoc = app.documents.add(UnitValue(layerBoundsW, "px"), UnitValue(layerBoundsH, "px"),
                                        exportDoc.resolution, "psd_full_export", NewDocumentMode.RGB, DocumentFill.TRANSPARENT);
                                    app.activeDocument = exportDoc;
                                    var dupLayer = layer.duplicate(tempDoc, ElementPlacement.PLACEATBEGINNING);
                                    app.activeDocument = tempDoc;
                                    tempDoc.activeLayer = dupLayer;
                                    var dup = tempDoc.activeLayer;
                                    var b2 = dup.bounds;
                                    dup.translate(-b2[0].as("px"), -b2[1].as("px"));
                                    if (targetDocW > 0 && targetDocH > 0 &&
                                        (targetDocW != tempDoc.width.as("px") || targetDocH != tempDoc.height.as("px"))) {
                                        tempDoc.resizeCanvas(UnitValue(targetDocW, "px"), UnitValue(targetDocH, "px"), AnchorPosition.MIDDLECENTER);
                                    }
                                    if (pngScale != 1) scaleImage();
                                    savePNG(saveFile);
                                    tempDoc.close(SaveOptions.DONOTSAVECHANGES);
                                    app.activeDocument = exportDoc;
                                }
                            } else {
                                if (pngScale != 1) scaleImage();
                                savePNG(new File(finalDir + attachmentName + ".png"));
                            }
                        }
                    }

                    // 5. 恢复状态 (UNDO)
                    exportDoc.activeHistoryState = preTrimState;
                    layer.visible = false;
                }

                // JSON
                var idx = -1;
                for(var z=0; z<layers.length; z++) { if(layers[z] == sourceLayer) { idx = z; break; } }
                var index = (layers.length - 1) - idx; 

                var treatAsText = false;
                try { treatAsText = (layer.typename == "ArtLayer" && layer.kind == LayerKind.TEXT && suffixType == "Normal"); } catch (typeErr) {}
                var textContent = "";
                var textSize = 0;
                var textColor = "";
                if (treatAsText) {
                    try {
                        // PS 2026 安全检查：确认图层引用仍然有效
                        var ti = null;
                        try { ti = layer.textItem; } catch (tiErr) { treatAsText = false; }
                        if (!ti) { treatAsText = false; }
                        else {
                            textContent = ti.contents;
                            textSize = getTextSizePx(layer, ti, exportDoc, pngScale);
                            textColor = "#" + rgbToHex(ti.color);
                        }
                    } catch (e) {
                        treatAsText = false;
                    }
                }

                var legacyItem = {
                    pngname: String(slotName || "").replace(/^\s+|\s+$/g, ""),
                    id: sourceLayerId,
                    index: index,
                    x: x,
                    y: y,
                    width: Math.round(width),
                    height: Math.round(height),
                    uiType: suffixType,
                    isText: treatAsText
                };
                if (usedPrecalc) {
                    legacyItem.precalc = true;
                    legacyItem.precalcOrigin = "bottom-left";
                }
                if (treatAsText) {
                    legacyItem.content = textContent;
                    legacyItem.fontSize = textSize;
                    legacyItem.fontColor = textColor;
                }
                legacyPngDataMap[skname].push(legacyItem);

                // PS 2026 兼容性：sourceLayer 引用可能在导图流程中失效（merge/rasterize 后）
                // 所有对 sourceLayer 的属性访问必须安全降级
                var sourcePath = "";
                var sourceBounds = { l:0, t:0, r:0, b:0 };
                var parentNodeId = null;
                var sourceLayerName = "";
                try {
                    sourcePath = getLayerSourcePath(sourceLayer);
                    sourceBounds = getLayerBoundsPx(sourceLayer);
                    sourceLayerName = layerName(sourceLayer);
                } catch (srcErr) {
                    // fallback: 用 rawName 和 slotName
                    sourcePath = sanitizePathSegment(rawName || slotName || "");
                    sourceBounds = { l:0, t:0, r:0, b:0 };
                    sourceLayerName = stripSuffix(trim(rawName), ".png").replace(/[:\/\\*\?\"\<\>\|]/g, "") || slotName;
                }
                try {
                    if (sourceLayer.parent && sourceLayer.parent.typename != "Document") parentNodeId = sourceLayer.parent.id;
                } catch (ignoredParent) {}

                var tagList = parseTagList(rawName);
                var assetId = "asset_" + sourceLayerId + "_" + index;
                var isExported = !!(shouldSave && width > 0 && height > 0);
                // 统一 trim + String() 防护：防止 PS ExtendScript 返回非标准类型
                var safePngName = String(slotName || "").replace(/^\s+|\s+$/g, "");
                var safeSourceLayerName = String(sourceLayerName || "").replace(/^\s+|\s+$/g, "");
                var assetRecord = {
                    assetId: assetId,
                    sourceNodeId: sourceLayerId,
                    parentNodeId: parentNodeId,
                    name: safeSourceLayerName,
                    pngName: safePngName,
                    exportPath: safePngName + ".png",
                    uiType: suffixType,
                    tagList: tagList,
                    absBounds: rectObjFromBounds(sourceBounds),
                    trimBounds: { x: x - width / 2, y: y - height / 2, width: Math.round(width), height: Math.round(height) },
                    sourcePath: sourcePath,
                    isExported: isExported,
                    // Text-layer fields (v2 fix): carry text info into assets[] so C# can create Text nodes
                    isText: !!treatAsText
                };
                if (treatAsText) {
                    assetRecord.textContent = textContent;
                    assetRecord.fontSize = textSize;
                    assetRecord.fontColor = textColor;
                }
                assets.push(assetRecord);

                var skeletonNode = skeletonByNodeId[sourceLayerId];
                if (skeletonNode) {
                    skeletonNode.exportAssetRef = assetId;
                    skeletonNode.hasVisualOutput = skeletonNode.hasVisualOutput || isExported;
                    skeletonNode.isStructureOnly = !skeletonNode.hasVisualOutput;
                }
            }
            skinIndex++;
        }
        var meta = {
            version: 2,
            schema: "psd2unity-export-v2",
            canvas: { width: canvasWidth, height: canvasHeight },
            psdName: decodeURI(originalDoc.name),
            generatedAtUtc: (new Date()).toUTCString(),
            pngScale: pngScale,
            trimWhitespace: trimWhitespace,
            onlyTagged: onlyTagged,
            warnings: exportWarnings
        };

        var jsonRoot = {
            meta: meta,
            assets: assets,
            skeleton: skeleton,
            canvas: meta.canvas,
            pngdata: legacyPngDataMap
        };
        var json = toPrettyJson(jsonRoot);

        // Write JSON
        if (writeJson) {
            var name = decodeURI(originalDoc.name);
            name = name.substring(0, name.indexOf("."));
            var file = new File(saveDir + name + ".ps.data");
            file.remove();
            file.open("w", "TEXT");
            file.lineFeed = "\n";
            file.encoding = "UTF-8";
            file.write(json);

            // Post-write validation: ensure generated JSON starts with { or [
            if (!json || (json.charAt(0) != '{' && json.charAt(0) != '[')) {
                alert("WARNING: Generated .ps.data is NOT valid JSON!\n" +
                      "File: " + file.fsName +
                      "\n\nFirst 80 characters:\n" + json.substring(0, 80) +
                      "\n\nThe PSD import in Unity will fail. Please report this bug.");
            }

            file.close();
        }

    } catch (e) {
        var errDetail = "" + e;
        var errLine = (e && e.line) ? "\nAt Line: " + e.line : "";
        // PS 2026 兼容性：executeActionGet("获取") 在图层引用失效时抛 "命令'获取'当前不可用"
        alert("Export Error: " + errDetail + errLine);
    } finally {
        if (exportDoc) exportDoc.close(SaveOptions.DONOTSAVECHANGES);
        app.activeDocument = originalDoc;
    }

    alert("Export Success!");
}

// =========================================================
// 【核心解耦】预计算所有 Layout 组的包围盒
// =========================================================
function preCalculateAllGroupBounds(doc) {
    var map = {};
    
    // 递归函数：计算节点 bounds
    // 如果是组，递归计算子节点 bounds 的并集
    // 如果是层，直接读 bounds
    function getBoundsRec(node) {
        var myBounds = { l: 99999, t: 99999, r: -99999, b: -99999, empty: true };
        
        // PS 2026 兼容性：单节点访问保护
        var nodeType = "";
        try { nodeType = node.typename; } catch (e) { return myBounds; }
        
        if (nodeType == "ArtLayer") {
            if (!node.allLocked) { // 简单检查
                var b = null;
                try { b = node.bounds; } catch (e) { return myBounds; }
                var l = b[0].as("px"); var t = b[1].as("px");
                var r = b[2].as("px"); var bot = b[3].as("px");
                if (r > l && bot > t) {
                    myBounds = { l:l, t:t, r:r, b:bot, empty:false };
                }
            }
        } 
        else if (nodeType == "LayerSet") {
            var childCount = 0;
            try { childCount = node.layers.length; } catch (e) { return myBounds; }
            for (var i = 0; i < childCount; i++) {
                var childBounds = getBoundsRec(node.layers[i]);
                if (!childBounds.empty) {
                    myBounds.empty = false;
                    if (childBounds.l < myBounds.l) myBounds.l = childBounds.l;
                    if (childBounds.t < myBounds.t) myBounds.t = childBounds.t;
                    if (childBounds.r > myBounds.r) myBounds.r = childBounds.r;
                    if (childBounds.b > myBounds.b) myBounds.b = childBounds.b;
                }
            }
            
            // 存入 Map (Key = Layer ID)
            if (!myBounds.empty) {
                map[node.id] = {
                    x: myBounds.l,
                    y: myBounds.t,
                    w: myBounds.r - myBounds.l,
                    h: myBounds.b - myBounds.t
                };
            } else {
                map[node.id] = { x:0, y:0, w:0, h:0 };
            }
        }
        return myBounds;
    }

    // 从根节点开始遍历
    for (var i = 0; i < doc.layers.length; i++) {
        getBoundsRec(doc.layers[i]);
    }
    
    return map;
}

// =========================================================
// Helpers (Standard)
// =========================================================

function collectSkeletonNodes(parent, parentNodeId, depth, parentPath, outList, mapByNodeId, canvasW, canvasH, warnings) {
    for (var i = 0; i < parent.layers.length; i++) {
        var layer = null;
        try { layer = parent.layers[i]; } catch (layerAccessErr) {
            pushWarning(warnings, "skeleton_layer_access_failed", layerAccessErr);
            continue;
        }
        if (!layer) continue;

        try {
            var rawName = safeLayerName(layer);
            var pathName = sanitizePathSegment(rawName);
            var sourcePath = parentPath ? (parentPath + "/" + pathName) : pathName;
            var layerTypeInfo = inferLayerTypeInfo(layer, rawName);
            var abs = getLayerBoundsPx(layer);
            var local = abs;
            if (parentNodeId != null && mapByNodeId[parentNodeId] && mapByNodeId[parentNodeId].absBounds) {
                var pAbs = mapByNodeId[parentNodeId].absBounds;
                local = {
                    l: abs.l - pAbs.x,
                    t: abs.t - pAbs.y,
                    r: abs.r - pAbs.x,
                    b: abs.b - pAbs.y
                };
            }

            var width = Math.max(0, abs.r - abs.l);
            var height = Math.max(0, abs.b - abs.t);
            var centerX = abs.l + width * 0.5;
            var centerY = canvasH - (abs.t + height * 0.5);
            var isGroup = (layer.typename == "LayerSet");
            var childrenCount = isGroup ? safeChildCount(layer) : 0;
            var hasVisualOutput = (!isGroup && width > 0 && height > 0);
            var nodeId = safeLayerId(layer);

            var node = {
                nodeId: nodeId,
                parentNodeId: parentNodeId,
                name: layerName(layer),
                rawLayerName: rawName,
                layerType: layerTypeInfo.layerType,
                sourcePath: sourcePath,
                depth: depth,
                siblingIndex: i,
                visible: safeLayerVisible(layer),
                opacity: getLayerOpacity(layer),
                absBounds: { x: abs.l, y: abs.t, width: width, height: height },
                localBounds: { x: local.l, y: local.t, width: Math.max(0, local.r - local.l), height: Math.max(0, local.b - local.t) },
                width: width,
                height: height,
                x: abs.l,
                y: abs.t,
                centerX: centerX,
                centerY: centerY,
                tagList: parseTagList(rawName),
                uiTypeHint: layerTypeInfo.uiTypeHint,
                layoutHint: layerTypeInfo.layoutHint,
                groupRoleHint: layerTypeInfo.groupRoleHint,
                exportAssetRef: "",
                hasVisualOutput: hasVisualOutput,
                isStructureOnly: !hasVisualOutput,
                childrenCount: childrenCount,
                isLeaf: childrenCount == 0,
                isGroup: isGroup
            };
            outList.push(node);
            if (nodeId != 0) mapByNodeId[nodeId] = node;

            if (isGroup) {
                collectSkeletonNodes(layer, nodeId, depth + 1, sourcePath, outList, mapByNodeId, canvasW, canvasH, warnings);
            }
        } catch (nodeErr) {
            // 单节点失败不影响整体导出
            pushWarning(warnings, "skeleton_node_failed@" + i, nodeErr);
            continue;
        }
    }
}

function inferLayerTypeInfo(layer, rawName) {
    var info = {
        layerType: layer.typename,
        uiTypeHint: "Normal",
        layoutHint: "None",
        groupRoleHint: (layer.typename == "LayerSet") ? "Group" : "Leaf"
    };
    if (layer.typename == "ArtLayer" && layer.kind == LayerKind.TEXT) {
        info.layerType = "Text";
        info.uiTypeHint = "Text";
    } else if (rawName.indexOf("@ImgNoTrim") != -1 || rawName.indexOf("@Img") != -1 || rawName.indexOf("@Bg") != -1) {
        info.uiTypeHint = "Image";
    } else if (rawName.indexOf("@Btn") != -1) {
        info.uiTypeHint = "Button";
        info.groupRoleHint = "ContainerCandidate";
    } else if (rawName.indexOf("@H") != -1) {
        info.uiTypeHint = "Horizontal";
        info.layoutHint = "Horizontal";
        info.groupRoleHint = "ContainerCandidate";
    } else if (rawName.indexOf("@V") != -1) {
        info.uiTypeHint = "Vertical";
        info.layoutHint = "Vertical";
        info.groupRoleHint = "ContainerCandidate";
    } else if (rawName.indexOf("@G") != -1) {
        info.uiTypeHint = "Grid";
        info.layoutHint = "Grid";
        info.groupRoleHint = "ContainerCandidate";
    } else if (rawName.indexOf("@Item") != -1) {
        info.uiTypeHint = "Item";
        info.groupRoleHint = "ContainerCandidate";
    }
    if (layer.typename == "LayerSet" && info.groupRoleHint == "Group") info.groupRoleHint = "GroupCandidate";
    return info;
}

function getLayerOpacity(layer) {
    try { return layer.opacity; } catch (e) { return 100; }
}

function getLayerBoundsPx(layer) {
    try {
        // PS 2026 兼容性：快速检测 layer 引用是否有效
        var b = layer.bounds;
        var l = b[0].as("px");
        var t = b[1].as("px");
        var r = b[2].as("px");
        var bot = b[3].as("px");
        return { l: l, t: t, r: r, b: bot };
    } catch (e) {
        return { l: 0, t: 0, r: 0, b: 0 };
    }
}

function rectObjFromBounds(b) {
    return { x: b.l, y: b.t, width: Math.max(0, b.r - b.l), height: Math.max(0, b.b - b.t) };
}

function sanitizePathSegment(name) {
    if (!name) return "";
    // 强制转字符串，防止 PS ExtendScript 返回非标准类型导致 .replace/.trim 链式调用断裂
    var s = String(name);
    s = s.replace(/[:\/\\*\?\"\<\>\|]/g, "");
    s = s.replace(/^\s+|\s+$/g, "");
    return s;
}

function safeLayerId(layer) {
    try { return layer.id; } catch (e) { return 0; }
}

function safeLayerName(layer) {
    try { return layer.name || ""; } catch (e) { return ""; }
}

function safeLayerVisible(layer) {
    try { return !!layer.visible; } catch (e) { return false; }
}

function safeChildCount(layer) {
    try { return layer.layers ? layer.layers.length : 0; } catch (e) { return 0; }
}

function getLayerSourcePath(layer) {
    // PS 2026 兼容性：layer 引用可能已失效（merge/rasterize/history-rollback 后）
    var safeName = "";
    try { safeName = layer.name || ""; } catch (e) { safeName = ""; }
    var path = sanitizePathSegment(safeName);
    var p = null;
    try { p = layer.parent; } catch (e) {}
    while (p) {
        var pName = "";
        var pType = "";
        try { pName = p.name || ""; } catch (e) { break; }
        try { pType = p.typename; } catch (e) { break; }
        if (pType == "Document") break;
        path = sanitizePathSegment(pName) + "/" + path;
        try { p = p.parent; } catch (e) { break; }
    }
    return path;
}

function parseTagList(rawName) {
    var tags = [];
    if (!rawName) return tags;
    var m = rawName.match(/@[A-Za-z0-9_]+/g);
    if (!m) return tags;
    for (var i = 0; i < m.length; i++) tags.push(m[i]);
    return tags;
}

function toPrettyJson(obj) {
    // Single reliable path: hand-written recursive serializer.
    // Zero dependency on any ExtendScript global object (JSON, toSource).
    // This follows the same "zero external API" principle as the old version's
    // string-concatenation approach, but supports arbitrary nesting depth.
    //
    // Root cause context: The previous 3-path fallback (JSON.stringify →
    // toSource()+regex → serializeObject) produced corrupted output in some
    // PS ExtendScript environments — missing braces, commas, and delimiters.
    // The old script (v8, ~539 lines) had NO serialization function at all;
    // it built JSON via pure string concatenation (json += "{...}") which was
    // 100% reliable. We revert to that same philosophy here.
    try {
        var json = serializeObject(obj);
        if (json && json.length >= 2) return json;
    } catch (e) {
        alert("Serialization Error: " + e);
    }

    // Absolute fallback — should never reach here
    return "{}";
}

// Minimal recursive JSON serializer for ExtendScript environments without JSON global
function serializeObject(o, depth) {
    if (depth === undefined) depth = 0;
    if (depth > 20) return "\"...\"";
    if (o === null) return "null";
    if (o === undefined) return "null";

    var t = typeof o;
    if (t == "string") return "\"" + o.replace(/\\/g,"\\\\").replace(/"/g,'\\"').replace(/\n/g,"\\n").replace(/\r/g,"") + "\"";
    if (t == "number") return isFinite(o) ? String(o) : "null";
    if (t == "boolean") return o ? "true" : "false";

    if (o instanceof Array || (t == "object" && o.length !== undefined)) {
        var parts = [];
        for (var i = 0; i < o.length; i++) parts.push(serializeObject(o[i], depth + 1));
        return "[" + parts.join(", ") + "]";
    }

    if (t == "object") {
        var parts2 = [];
        for (var k in o) {
            if (!o.hasOwnProperty(k)) continue;
            var val = o[k];
            if (val === undefined) continue;  // Skip undefined — same behavior as JSON.stringify
            parts2.push("\"" + k + "\": " + serializeObject(val, depth + 1));
        }
        return "{" + parts2.join(", ") + "}";
    }

    return "null";
}

function pushWarning(warnings, code, err) {
    if (!warnings) return;
    if (warnings.length >= 50) return; // 防止异常风暴导致 JSON 过大
    var msg = "";
    try { msg = err ? ("" + err) : ""; } catch (ignored) {}
    warnings.push({ code: code, message: msg });
}

function collectLayers(parent, collect, onlyTagged) {
    var len = 0;
    try { len = parent.layers.length; } catch (e) { return; }
    for (var i = 0; i < len; i++) {
        var layer = null;
        try { layer = parent.layers[i]; } catch (e) { continue; }
        if (!layer) continue;
        try {
            if (ignoreHiddenLayers && !layer.visible) continue;
            if (layer.typename == "ArtLayer") { if (layer.bounds[2] == 0 && layer.bounds[3] == 0) continue; }

            if (!onlyTagged) {
                if (layer.typename == "ArtLayer") {
                    collect.push(layer);
                } else if (layer.typename == "LayerSet") {
                    collectLayers(layer, collect, onlyTagged);
                }
                continue;
            }

            var name = layer.name;
            var hasTag = (name.indexOf("@") != -1);
            var isAtomic = (name.indexOf("@Img") != -1 || name.indexOf("@Bg") != -1 || name.indexOf("@ImgNoTrim") != -1);
            var isContainer = (name.indexOf("@Btn") != -1 || name.indexOf("@H") != -1 || name.indexOf("@V") != -1 || name.indexOf("@G") != -1 || name.indexOf("@Item") != -1);

            if (hasTag) {
                if (isAtomic) collect.push(layer);
                else if (isContainer) { collect.push(layer); if (layer.typename == "LayerSet") collectLayers(layer, collect, onlyTagged); }
                else if (layer.typename == "LayerSet") collectLayers(layer, collect, onlyTagged);
                else collect.push(layer);
        } else {
            if (layer.typename == "ArtLayer" && layer.kind == LayerKind.TEXT) {
                collect.push(layer);
            } else if (layer.typename == "LayerSet") {
                collectLayers(layer, collect, onlyTagged);
            }
        }
        } catch (e) {}
    }
}

function hideAllLayers(doc) {
    function recurseHide(parent) {
        var len = 0;
        try { len = parent.layers.length; } catch (e) { return; }
        for (var i=0; i<len; i++) {
            try {
                parent.layers[i].visible = false;
                if (parent.layers[i].typename == "LayerSet") recurseHide(parent.layers[i]);
            } catch (e) {}
        }
    }
    recurseHide(doc);
}

function makeLayerVisible(layer) {
    try { layer.visible = true; } catch (e) {}
    var isSet = false;
    try { isSet = (layer.typename == "LayerSet"); } catch (e) { return; }
    if (isSet) setLayerSetChildrenVisible(layer);
    var p = null;
    try { p = layer.parent; } catch (e) { return; }
    while (p) {
        try { p.visible = true; } catch (e2) { break; }
        try { if (p.typename == "Document") break; } catch (e3) { break; }
        try { p = p.parent; } catch (e4) { break; }
    }
}

function setLayerSetChildrenVisible(layerSet) {
    var len = 0;
    try { len = layerSet.layers.length; } catch (e) { return; }
    for (var i = 0; i < len; i++) {
        var child = null;
        try { child = layerSet.layers[i]; } catch (e) { continue; }
        if (!child) continue;
        try {
            if (ignoreHiddenLayers) {
                child.visible = (visibleMap[child.id] === true);
            } else {
                child.visible = true;
            }
            var childIsSet = (child.typename == "LayerSet");
            if (childIsSet) setLayerSetChildrenVisible(child);
        } catch (e) {}
    }
}

function cacheVisibleState(doc) {
    visibleMap = {};
    for (var i = 0; i < doc.layers.length; i++) {
        cacheVisibleStateRec(doc.layers[i]);
    }
}

function getGroupBoundsIgnoringOutliers(layerSet) {
    var items = [];
    collectVisibleLeafBounds(layerSet, items);
    if (items.length == 0) {
        try { return layerSet.bounds; } catch (e) { return boundsToArray({l:0,t:0,r:0,b:0}); }
    }

    var mainBounds = null;
    for (var i = 0; i < items.length; i++) {
        var it = items[i];
        if (!it.isTiny) {
            mainBounds = unionBounds(mainBounds, it.bounds);
        }
    }
    if (mainBounds == null) {
        // all tiny, fallback to full union
        for (var j = 0; j < items.length; j++) {
            mainBounds = unionBounds(mainBounds, items[j].bounds);
        }
        return boundsToArray(mainBounds);
    }

    var finalBounds = mainBounds;
    for (var k = 0; k < items.length; k++) {
        var it2 = items[k];
        if (!it2.isTiny) continue;
        var dist = rectDistance(mainBounds, it2.bounds);
        if (dist <= tinyOutlierMaxDistance) {
            finalBounds = unionBounds(finalBounds, it2.bounds);
        }
    }
    return boundsToArray(finalBounds);
}

function collectVisibleLeafBounds(layer, outArr) {
    if (!layer || !outArr) return;
    if (!layer.visible) return;

    if (layer.typename == "ArtLayer") {
        try {
            var b = layer.bounds;
            var l = b[0].as("px");
            var t = b[1].as("px");
            var r = b[2].as("px");
            var bot = b[3].as("px");
            var w = Math.max(0, r - l);
            var h = Math.max(0, bot - t);
            if (w <= 0 || h <= 0) return;
            var area = w * h;
            var isTiny = (area <= tinyOutlierArea);
            outArr.push({ bounds: { l: l, t: t, r: r, b: bot }, isTiny: isTiny });
        } catch (e) {}
        return;
    }

    if (layer.typename == "LayerSet") {
        for (var i = 0; i < layer.layers.length; i++) {
            collectVisibleLeafBounds(layer.layers[i], outArr);
        }
    }
}

function unionBounds(a, b) {
    if (!a && b) return { l: b.l, t: b.t, r: b.r, b: b.b };
    if (!b && a) return { l: a.l, t: a.t, r: a.r, b: a.b };
    if (!a && !b) return null;
    return {
        l: Math.min(a.l, b.l),
        t: Math.min(a.t, b.t),
        r: Math.max(a.r, b.r),
        b: Math.max(a.b, b.b)
    };
}

function rectDistance(a, b) {
    var dx = 0;
    if (b.r < a.l) dx = a.l - b.r;
    else if (b.l > a.r) dx = b.l - a.r;
    var dy = 0;
    if (b.b < a.t) dy = a.t - b.b;
    else if (b.t > a.b) dy = b.t - a.b;
    return Math.sqrt(dx * dx + dy * dy);
}

function boundsToArray(b) {
    // return array-like object with as("px") compatible access
    return [
        UnitValue(b.l, "px"),
        UnitValue(b.t, "px"),
        UnitValue(b.r, "px"),
        UnitValue(b.b, "px")
    ];
}

function cacheVisibleStateRec(layer) {
    try { visibleMap[layer.id] = layer.visible; } catch (e) {}
    var isSet = false;
    try { isSet = (layer.typename == "LayerSet"); } catch (e) {}
    if (isSet) {
        var len = 0;
        try { len = layer.layers.length; } catch (e) {}
        for (var i = 0; i < len; i++) {
            try { cacheVisibleStateRec(layer.layers[i]); } catch (e) {}
        }
    }
}

function prepareLayerForExport(layer, suffixType) {
    if (suffixType != "Image") return layer;
    try {
        if (layer.typename == "LayerSet") {
            exportDoc.activeLayer = layer;
            return layer.merge();
        }
        if (layer.kind == LayerKind.SMARTOBJECT) {
            exportDoc.activeLayer = layer;
            layer.rasterize(RasterizeType.ENTIRELAYER);
        }
        if (layer.typename == "ArtLayer" &&
            layer.kind != LayerKind.NORMAL &&
            layer.kind != LayerKind.TEXT &&
            layer.kind != LayerKind.SMARTOBJECT) {
            exportDoc.activeLayer = layer;
            layer.rasterize(RasterizeType.ENTIRELAYER);
        }
    } catch (e) {}
    return layer;
}

function padToMultipleOf4(value) {
    if (value <= 0) return 0;
    return Math.ceil(value / 4) * 4;
}

function exportSmartObjectTrimmed(layer, file) {
    var info = null;
    try {
        app.activeDocument = exportDoc;
        exportDoc.activeLayer = layer;
        var editId = stringIDToTypeID("placedLayerEditContents");
        executeAction(editId, undefined, DialogModes.NO);
        var soDoc = app.activeDocument;

        var soW = soDoc.width.as("px");
        var soH = soDoc.height.as("px");
        if (soDoc.layers.length > 1) {
            try { soDoc.mergeVisibleLayers(); } catch (e) {}
        }
        var active = soDoc.activeLayer;
        var b = active.bounds;
        var l = b[0].as("px");
        var t = b[1].as("px");
        var r = b[2].as("px");
        var bot = b[3].as("px");
        var trimW = Math.max(0, r - l);
        var trimH = Math.max(0, bot - t);
        var left = l;
        var top = t;
        var right = Math.max(0, soW - r);
        var bottom = Math.max(0, soH - bot);

        if (trimW > 0 && trimH > 0) {
            active.translate(-l, -t);
            soDoc.resizeCanvas(UnitValue(trimW, "px"), UnitValue(trimH, "px"), AnchorPosition.TOPLEFT);
        }

        var finalW = padToMultipleOf4(trimW * pngScale);
        var finalH = padToMultipleOf4(trimH * pngScale);
        if (finalW > 0 && finalH > 0) {
            var finalDocW = Math.round(finalW / pngScale);
            var finalDocH = Math.round(finalH / pngScale);
            if (finalDocW > 0 && finalDocH > 0 &&
                (finalDocW != soDoc.width.as("px") || finalDocH != soDoc.height.as("px"))) {
                soDoc.resizeCanvas(UnitValue(finalDocW, "px"), UnitValue(finalDocH, "px"), AnchorPosition.MIDDLECENTER);
            }
        }

        if (pngScale != 1) scaleImage();
        if (file) savePNG(file);

        info = { soW: soW, soH: soH, trimW: trimW, trimH: trimH, left: left, top: top, right: right, bottom: bottom, finalW: finalW, finalH: finalH };
        soDoc.close(SaveOptions.DONOTSAVECHANGES);
    } catch (e) {
        try {
            if (app.activeDocument != exportDoc) {
                app.activeDocument.close(SaveOptions.DONOTSAVECHANGES);
            }
        } catch (ignored) {}
    } finally {
        app.activeDocument = exportDoc;
    }
    return info;
}

// UI Fix: Ensure correct parent-child add
function showDialog() {
    if (!originalDoc) { alert("Open Doc First"); return; }
    
    var dlg = new Window("dialog", "ExportToPNG v8");
    dlg.alignChildren = "fill";
    
    // Main Group (Row)
    var main = dlg.add("group");
    main.orientation = "row";
    main.alignChildren = "top";
    
    // Col 1
    var p1 = main.add("panel", undefined, "Settings");
    p1.alignChildren = "left";
    var chkPng = p1.add("checkbox", undefined, " Write PNGs"); chkPng.value = writePngs;
    var chkTpl = p1.add("checkbox", undefined, " Write Template"); chkTpl.value = writeTemplate;
    var chkJson = p1.add("checkbox", undefined, " Write JSON"); chkJson.value = writeJson;
    
    // Col 2
    var p2 = main.add("panel", undefined, "Options");
    p2.alignChildren = "left";
    var chkIgnore = p2.add("checkbox", undefined, " Ignore Hidden"); chkIgnore.value = ignoreHiddenLayers;
    var chkGroups = p2.add("checkbox", undefined, " Use Groups"); chkGroups.value = groupsAsSkins;
    var chkTrim = p2.add("checkbox", undefined, " Trim Whitespace"); chkTrim.value = trimWhitespace;
    var chkTagged = p2.add("checkbox", undefined, " Only Export @ Tagged"); chkTagged.value = onlyTagged;

    var grpScale = dlg.add("group");
    grpScale.add("statictext", undefined, "PNG Scale:");
    var txtScale = grpScale.add("edittext", undefined, pngScale * 100); txtScale.characters = 4;
    grpScale.add("statictext", undefined, "%");

    var pPath = dlg.add("panel", undefined, "Output");
    pPath.orientation = "row";
    var txtPath = pPath.add("edittext", undefined, saveDir); txtPath.preferredSize = [200, 20];
    var btnBrowse = pPath.add("button", undefined, "Browse");
    btnBrowse.onClick = function() { var f=Folder.selectDialog(); if(f) txtPath.text=f.fsName; }

    var grpBtn = dlg.add("group");
    grpBtn.alignment = "center";
    var btnOk = grpBtn.add("button", undefined, "Export");
    var btnCancel = grpBtn.add("button", undefined, "Cancel");

    btnOk.onClick = function() {
        writePngs = chkPng.value;
        writeTemplate = chkTpl.value;
        writeJson = chkJson.value;
        ignoreHiddenLayers = chkIgnore.value;
        groupsAsSkins = chkGroups.value;
        trimWhitespace = chkTrim.value;
        onlyTagged = writeJson ? true : chkTagged.value;
        pngScale = parseFloat(txtScale.text) / 100;
        saveDir = txtPath.text;
        dlg.close(1);
        run();
    };
    btnCancel.onClick = function() { dlg.close(0); };
    
    function refreshTaggedState() {
        if (chkJson.value) {
            chkTagged.value = true;
            chkTagged.enabled = false;
        } else {
            chkTagged.enabled = true;
        }
    }
    chkJson.onClick = refreshTaggedState;
    refreshTaggedState();

    dlg.center();
    dlg.show();
}

function loadSettings() {
    try { settings = app.getCustomOptions(settingsID); } catch (e) { return; }
    if (settings.hasKey(saveDirID)) saveDir = settings.getString(saveDirID);
    if (settings.hasKey(onlyTaggedID)) onlyTagged = settings.getBoolean(onlyTaggedID);
}
function saveSettings() {
    var s = new ActionDescriptor();
    s.putString(saveDirID, saveDir);
    s.putBoolean(onlyTaggedID, onlyTagged);
    app.putCustomOptions(settingsID, s, true);
}
function scaleImage() { activeDocument.resizeImage(UnitValue(activeDocument.width.as("px")*pngScale,"px"), null, 300, ResampleMethod.BICUBICSHARPER); }
function deleteDocumentAncestorsMetadata() {}
function hasFilePath() { return originalDoc.path; }
function countAssocArray(obj) { var c=0; for(var k in obj)c++; return c; }
function trim(s) { return s.replace(/^\s+|\s+$/g, ""); }
function stripSuffix(str, suffix) { if (endsWith(str.toLowerCase(), suffix.toLowerCase())) str = str.substring(0, str.length - suffix.length); return str; }
function layerName(layer) {
    // PS 2026 兼容性
    var safeName = "";
    try { safeName = layer.name || ""; } catch (e) { return "unknown_layer"; }
    // 强制转字符串，防止 PS ExtendScript 返回非标准类型导致链式 .replace/.trim 断裂
    safeName = String(safeName);
    safeName = safeName.replace(/^\s+|\s+$/g, "");          // trim 前后空白
    safeName = stripSuffix(safeName, ".png");               // strip 后缀
    safeName = safeName.replace(/[:\/\\*\?\"\<\>\|]/g, ""); // 去非法字符
    return safeName;
}
function getTextSizePx(layer, textItem, doc, scale) {
    var size = 0;
    var sizeBase = 0;
    var res = 72;
    var uvType = null;
    var uvValue = null;
    var uvAsPt = null;
    var uvAsPx = null;
    try {
        var uv = textItem.size;
        if (doc && doc.resolution) res = doc.resolution;
        if (uv) {
            if (uv.type != undefined) {
                uvType = "" + uv.type;
                var t = uvType.toLowerCase();
                if (uv.value != undefined) uvValue = uv.value;
                if (uv.as) {
                    try { uvAsPt = uv.as("pt"); } catch (e) {}
                    try { uvAsPx = uv.as("px"); } catch (e) {}
                }
                if (t == "px" || t == "pixel" || t == "pixels") {
                    size = uvValue;
                } else if (t == "pt" || t == "point" || t == "points") {
                    size = uvValue * res / 72;
                } else if (uvAsPx != null) {
                    size = uvAsPx;
                } else if (uvValue != undefined) {
                    size = uvValue;
                }
            } else {
                var basePt = 0;
                if (uv.as) {
                    try { uvAsPt = uv.as("pt"); } catch (e) {}
                    try { uvAsPx = uv.as("px"); } catch (e) {}
                }
                if (uv.value != undefined) uvValue = uv.value;
                if (uvAsPt != null) {
                    basePt = uvAsPt;
                } else if (uvValue != undefined) {
                    basePt = uvValue;
                }
                if (basePt) {
                    size = basePt * res / 72;
                }
            }
        }
    } catch (e) {}

    if (!size || isNaN(size)) {
        try { size = parseFloat(textItem.size); } catch (e) { size = 0; }
    }
    if (!size || isNaN(size)) size = 24;

    var vScale = 1;
    try { if (textItem.verticalScale != undefined) vScale = textItem.verticalScale / 100.0; } catch (e) {}
    sizeBase = size;
    size = size * vScale;

    var tScale = 1;
    try { tScale = getTextLayerTransformScale(layer); } catch (e) { tScale = 1; }
    size = size * tScale;
    if (scale && scale != 1) size = size * scale;
    size = Math.round(size * 10) / 10;

    return size;
}

function getTextLayerTransformScale(layer) {
    var scale = 1;
    try {
        // PS 2026 兼容性检查：layer 对象可能已失效（merge/rasterize/history-rollback 后）
        if (!layer || !layer.id || layer.id === 0) return 1;
        // 额外校验：确认图层仍存在于文档中（通过 try bounds 访问快速检测）
        try { var _testBounds = layer.bounds; } catch (boundsErr) { return 1; }

        var ref = new ActionReference();
        ref.putIdentifier(charIDToTypeID("Lyr "), layer.id);
        var desc = executeActionGet(ref);
        if (desc.hasKey(stringIDToTypeID("textKey"))) {
            var textKey = desc.getObjectValue(stringIDToTypeID("textKey"));
            if (textKey.hasKey(stringIDToTypeID("transform"))) {
                var tr = textKey.getObjectValue(stringIDToTypeID("transform"));
                var xx = tr.getDouble(stringIDToTypeID("xx"));
                var xy = tr.getDouble(stringIDToTypeID("xy"));
                var yx = tr.getDouble(stringIDToTypeID("yx"));
                var yy = tr.getDouble(stringIDToTypeID("yy"));
                var sx = Math.sqrt(xx * xx + xy * xy);
                var sy = Math.sqrt(yy * yy + yx * yx);
                scale = (sx + sy) * 0.5;
            }
        }
    } catch (e) {}
    if (!scale || isNaN(scale)) scale = 1;
    return scale;
}
function allocateUniquePngName(baseName, dir, usedMap) {
    if (!usedMap[dir]) usedMap[dir] = {};
    if (!usedMap[dir][baseName]) {
        usedMap[dir][baseName] = true;
        return baseName;
    }
    var index = 1;
    while (true) {
        var candidate = baseName + "_" + index;
        if (!usedMap[dir][candidate]) {
            usedMap[dir][candidate] = true;
            return candidate;
        }
        index++;
    }
}
function endsWith(str, suffix) { return str.indexOf(suffix, str.length - suffix.length) !== -1; }
function savePNG(file) { var o=new PNGSaveOptions(); o.compression=6; activeDocument.saveAs(file, o, true, Extension.LOWERCASE); }
function escapeForJson(s) { if(!s)return""; return s.toString().replace(/\\/g,'\\\\').replace(/"/g,'\\"').replace(/\n/g,'\\n').replace(/\r/g,''); }
function rgbToHex(c) { try{var r=Math.round(c.rgb.red);var g=Math.round(c.rgb.green);var b=Math.round(c.rgb.blue);var n=r<<16|g<<8|b;return(function(h){return new Array(7-h.length).join("0")+h})(n.toString(16).toUpperCase());}catch(e){return"FFFFFF";} }
