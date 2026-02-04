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

var psdName = "";
var originalDoc;
var exportDoc; 

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

        // =========================================================
        // 【关键步骤 B】常规图层收集
        // =========================================================
        var layers = [];
        collectLayers(exportDoc, layers);

        // 初始全隐藏 (这是原版逻辑的起点)
        hideAllLayers(exportDoc);

        var json = '{\n"canvas":{';
        json += '"width":' + exportDoc.width.as("px") + "," + '"height":' + exportDoc.height.as("px") + "},";
        json += '\n"pngdata":{\n';

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
            
            json += '\t"';
            var skname = (skinName == "root") ? "root" : skinName;
            skname = skname.replace(/\/+/g, "/"); 
            if(skname == "root") skname = "root/"; else skname += "/";
            json += skname + '":\n\t[\n';

            for (var i = skinLayers.length - 1; i >= 0; i--) {
                var layer = skinLayers[i];
                var slotName = layerName(layer);
                
                // 类型判断
                var rawName = layer.name;
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

                var isLayerSet = (layer.typename == "LayerSet");

                // --- 坐标逻辑分流 ---
                var x = 0, y = 0, width = 0, height = 0;
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
                    layer = prepareLayerForExport(layer, suffixType);

                    if (useLayerBounds) {
                        var b = layer.bounds;
                        var l = b[0].as("px");
                        var t = b[1].as("px");
                        var r = b[2].as("px");
                        var bot = b[3].as("px");
                        var rawW = Math.max(0, r - l);
                        var rawH = Math.max(0, bot - t);
                        width = rawW * pngScale;
                        height = rawH * pngScale;
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

                    if (shouldSave) {
                        if (width > 0 && height > 0) {
                            var attachmentName = slotName;
                            var finalDir = saveDir;
                            if (groupsAsSkins && skinName != "root") {
                                finalDir = saveDir + skinName + "/";
                                new Folder(finalDir).create();
                            }

                            if (useLayerBounds) {
                                var tempDoc = app.documents.add(UnitValue(width / pngScale, "px"), UnitValue(height / pngScale, "px"),
                                    exportDoc.resolution, "psd_full_export", NewDocumentMode.RGB, DocumentFill.TRANSPARENT);
                                app.activeDocument = exportDoc;
                                var dupLayer = layer.duplicate(tempDoc, ElementPlacement.PLACEATBEGINNING);
                                app.activeDocument = tempDoc;
                                tempDoc.activeLayer = dupLayer;
                                var dup = tempDoc.activeLayer;
                                var b2 = dup.bounds;
                                dup.translate(-b2[0].as("px"), -b2[1].as("px"));
                                if (pngScale != 1) scaleImage();
                                savePNG(new File(finalDir + attachmentName + ".png"));
                                tempDoc.close(SaveOptions.DONOTSAVECHANGES);
                                app.activeDocument = exportDoc;
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
                for(var z=0; z<layers.length; z++) { if(layers[z] == layer) { idx = z; break; } }
                var index = (layers.length - 1) - idx; 

                var jsonDataItem = '\t\t{"pngname":"' + slotName + '"';
                jsonDataItem += ',"id":' + layer.id;
                jsonDataItem += ',"index":' + index;
                jsonDataItem += ',"x":' + x;
                jsonDataItem += ',"y":' + y;
                jsonDataItem += ',"width":' + Math.round(width);
                jsonDataItem += ',"height":' + Math.round(height);
                jsonDataItem += ',"uiType":"' + suffixType + '"';
                if (usedPrecalc) jsonDataItem += ',"precalc":true,"precalcOrigin":"bottom-left"';

                var treatAsText = (layer.typename == "ArtLayer" && layer.kind == LayerKind.TEXT && suffixType == "Normal");
                if (treatAsText) {
                    try {
                        var ti = layer.textItem;
                        var txtContent = escapeForJson(ti.contents);
                        var txtSize = 24;
                        try { txtSize = ti.size.as("px"); } catch (e) { txtSize = parseFloat(ti.size); }
                        var txtColor = rgbToHex(ti.color);
                        jsonDataItem += ',"isText":true,"content":"' + txtContent + '","fontSize":' + txtSize + ',"fontColor":"#' + txtColor + '"';
                    } catch (e) { jsonDataItem += ',"isText":false'; }
                } else { jsonDataItem += ',"isText":false'; }

                jsonDataItem += '}';
                json += jsonDataItem;
                json += (i > 0) ? ",\n" : "\n";
            }
            json += "\t\]";
            skinIndex++;
            json += (skinIndex < totalSkins) ? ",\n" : "\n";
        }

        json += '}\n}';

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
            file.close();
        }

    } catch (e) {
        alert("Export Error: " + e);
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
        
        if (node.typename == "ArtLayer") {
            if (!node.allLocked) { // 简单检查
                var b = node.bounds;
                var l = b[0].as("px"); var t = b[1].as("px");
                var r = b[2].as("px"); var bot = b[3].as("px");
                if (r > l && bot > t) {
                    myBounds = { l:l, t:t, r:r, b:bot, empty:false };
                }
            }
        } 
        else if (node.typename == "LayerSet") {
            for (var i = 0; i < node.layers.length; i++) {
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

function collectLayers(parent, collect) {
    for (var i = 0; i < parent.layers.length; i++) {
        var layer = parent.layers[i];
        if (ignoreHiddenLayers && !layer.visible) continue;
        if (layer.typename == "ArtLayer") { if (layer.bounds[2] == 0 && layer.bounds[3] == 0) continue; }

        var name = layer.name;
        var hasTag = (name.indexOf("@") != -1);
        var isAtomic = (name.indexOf("@Img") != -1 || name.indexOf("@Bg") != -1 || name.indexOf("@ImgNoTrim") != -1);
        var isContainer = (name.indexOf("@Btn") != -1 || name.indexOf("@H") != -1 || name.indexOf("@V") != -1 || name.indexOf("@G") != -1 || name.indexOf("@Item") != -1);

        if (hasTag) {
            if (isAtomic) collect.push(layer);
            else if (isContainer) { collect.push(layer); if (layer.typename == "LayerSet") collectLayers(layer, collect); }
            else if (layer.typename == "LayerSet") collectLayers(layer, collect);
            else collect.push(layer);
        } else {
            if (layer.typename == "ArtLayer" && layer.kind == LayerKind.TEXT) {
                collect.push(layer);
            } else if (layer.typename == "LayerSet") {
                collectLayers(layer, collect);
            }
        }
    }
}

function hideAllLayers(doc) {
    function recurseHide(parent) {
        for (var i=0; i<parent.layers.length; i++) {
            parent.layers[i].visible = false;
            if (parent.layers[i].typename == "LayerSet") recurseHide(parent.layers[i]);
        }
    }
    recurseHide(doc);
}

function makeLayerVisible(layer) {
    layer.visible = true;
    if (layer.typename == "LayerSet") setLayerSetChildrenVisible(layer);
    var parent = layer.parent;
    while (parent && parent.typename != "Document") {
        parent.visible = true;
        parent = parent.parent;
    }
}

function setLayerSetChildrenVisible(layerSet) {
    for (var i = 0; i < layerSet.layers.length; i++) {
        var child = layerSet.layers[i];
        child.visible = true;
        if (child.typename == "LayerSet") setLayerSetChildrenVisible(child);
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
        pngScale = parseFloat(txtScale.text) / 100;
        saveDir = txtPath.text;
        dlg.close(1);
        run();
    };
    btnCancel.onClick = function() { dlg.close(0); };
    
    dlg.center();
    dlg.show();
}

function loadSettings() { try{settings=app.getCustomOptions(settingsID);}catch(e){return;} if(settings.hasKey(saveDirID)) saveDir=settings.getString(saveDirID); }
function saveSettings() { var s=new ActionDescriptor(); s.putString(saveDirID, saveDir); app.putCustomOptions(settingsID, s, true); }
function scaleImage() { activeDocument.resizeImage(UnitValue(activeDocument.width.as("px")*pngScale,"px"), null, 300, ResampleMethod.BICUBICSHARPER); }
function deleteDocumentAncestorsMetadata() {}
function hasFilePath() { return originalDoc.path; }
function countAssocArray(obj) { var c=0; for(var k in obj)c++; return c; }
function trim(s) { return s.replace(/^\s+|\s+$/g, ""); }
function stripSuffix(str, suffix) { if (endsWith(str.toLowerCase(), suffix.toLowerCase())) str = str.substring(0, str.length - suffix.length); return str; }
function layerName(layer) { return stripSuffix(trim(layer.name), ".png").replace(/[:\/\\*\?\"\<\>\|]/g, ""); }
function endsWith(str, suffix) { return str.indexOf(suffix, str.length - suffix.length) !== -1; }
function savePNG(file) { var o=new PNGSaveOptions(); o.compression=6; activeDocument.saveAs(file, o, true, Extension.LOWERCASE); }
function escapeForJson(s) { if(!s)return""; return s.toString().replace(/\\/g,'\\\\').replace(/"/g,'\\"').replace(/\n/g,'\\n').replace(/\r/g,''); }
function rgbToHex(c) { try{var r=Math.round(c.rgb.red);var g=Math.round(c.rgb.green);var b=Math.round(c.rgb.blue);var n=r<<16|g<<8|b;return(function(h){return new Array(7-h.length).join("0")+h})(n.toString(16).toUpperCase());}catch(e){return"FFFFFF";} }
