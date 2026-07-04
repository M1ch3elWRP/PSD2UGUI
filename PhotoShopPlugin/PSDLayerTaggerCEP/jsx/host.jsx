#target photoshop

(function () {
    if (!$._PSDLayerTagger) {
        $._PSDLayerTagger = {};
    }

    var api = $._PSDLayerTagger;
    var c2t = charIDToTypeID;
    var s2t = stringIDToTypeID;
    var knownTags = [
        "@ImgNoTrim",
        "@ItemCircle",
        "@ItemBox",
        "@PopUp",
        "@StdBtn",
        "@ScrollRect",
        "@CommonSpriteWhite",
        "@CommonSprite",
        "@Item",
        "@Btn",
        "@Img",
        "@H",
        "@V",
        "@G"
    ];

    api.applyTag = function (tag, mode) {
        try {
            ensureDocument();
            api._pending = { action: "applyTag", tag: String(tag || ""), mode: String(mode || "set") };
            api._result = null;
            app.activeDocument.suspendHistory("PSD2NGUI Tag " + api._pending.tag, "$._PSDLayerTagger._runPending()");
            return toJson(api._result || ok("No change."));
        } catch (e) {
            return toJson(fail(e.message || String(e)));
        }
    };

    api.clearTags = function () {
        try {
            ensureDocument();
            api._pending = { action: "clearTags" };
            api._result = null;
            app.activeDocument.suspendHistory("PSD2NGUI Clear Tags", "$._PSDLayerTagger._runPending()");
            return toJson(api._result || ok("No change."));
        } catch (e) {
            return toJson(fail(e.message || String(e)));
        }
    };

    api.getSelectionInfo = function () {
        try {
            ensureDocument();
            var ids = getSelectedLayerIds();
            var layers = [];
            for (var i = 0; i < ids.length; i++) {
                var name = getLayerNameById(ids[i]);
                layers.push({
                    id: ids[i],
                    name: name,
                    cleanName: stripKnownTags(name),
                    tags: extractTags(name)
                });
            }

            return toJson({
                ok: true,
                count: layers.length,
                layers: layers
            });
        } catch (e) {
            return toJson(fail(e.message || String(e)));
        }
    };

    api._runPending = function () {
        var pending = api._pending || {};
        if (pending.action === "applyTag") {
            api._result = runApplyTag(pending.tag, pending.mode);
        } else if (pending.action === "clearTags") {
            api._result = runClearTags();
        } else {
            api._result = fail("Unknown action.");
        }
    };

    function runApplyTag(tag, mode) {
        if (!tag) return fail("Tag is empty.");

        var ids = getSelectedLayerIds();
        if (ids.length === 0) return fail("没有选中图层。");

        var changed = 0;
        var names = [];
        for (var i = 0; i < ids.length; i++) {
            var oldName = getLayerNameById(ids[i]);
            var newName = buildTaggedName(oldName, tag, mode);
            if (oldName !== newName) {
                setLayerNameById(ids[i], newName);
                changed++;
            }
            names.push(newName);
        }

        return {
            ok: true,
            changed: changed,
            count: ids.length,
            names: names,
            message: "已给 " + ids.length + " 个图层写入 " + tag
        };
    }

    function runClearTags() {
        var ids = getSelectedLayerIds();
        if (ids.length === 0) return fail("没有选中图层。");

        var changed = 0;
        var names = [];
        for (var i = 0; i < ids.length; i++) {
            var oldName = getLayerNameById(ids[i]);
            var newName = stripKnownTags(oldName);
            if (oldName !== newName) {
                setLayerNameById(ids[i], newName);
                changed++;
            }
            names.push(newName);
        }

        return {
            ok: true,
            changed: changed,
            count: ids.length,
            names: names,
            message: "已清除 " + changed + " 个图层的 PSD 标签"
        };
    }

    function ensureDocument() {
        if (!app.documents || app.documents.length === 0) {
            throw new Error("没有打开 PSD 文档。");
        }
    }

    function getSelectedLayerIds() {
        var ids = [];
        try {
            var ref = new ActionReference();
            ref.putProperty(c2t("Prpr"), s2t("targetLayersIDs"));
            ref.putEnumerated(c2t("Dcmn"), c2t("Ordn"), c2t("Trgt"));
            var desc = executeActionGet(ref);
            var key = s2t("targetLayersIDs");
            if (desc.hasKey(key)) {
                var list = desc.getList(key);
                for (var i = 0; i < list.count; i++) {
                    ids.push(list.getReference(i).getIdentifier(s2t("layerID")));
                }
            }
        } catch (e) {}

        if (ids.length === 0) {
            try {
                ids.push(app.activeDocument.activeLayer.id);
            } catch (ignored) {}
        }

        return ids;
    }

    function getLayerNameById(layerId) {
        var ref = new ActionReference();
        ref.putProperty(c2t("Prpr"), c2t("Nm  "));
        ref.putIdentifier(c2t("Lyr "), layerId);
        return executeActionGet(ref).getString(c2t("Nm  "));
    }

    function setLayerNameById(layerId, name) {
        var desc = new ActionDescriptor();
        var ref = new ActionReference();
        ref.putIdentifier(c2t("Lyr "), layerId);
        desc.putReference(c2t("null"), ref);

        var layerDesc = new ActionDescriptor();
        layerDesc.putString(c2t("Nm  "), name);
        desc.putObject(c2t("T   "), c2t("Lyr "), layerDesc);
        executeAction(c2t("setd"), desc, DialogModes.NO);
    }

    function buildTaggedName(name, tag, mode) {
        var base;
        if (mode === "append") {
            base = stripOneTag(name, tag);
        } else {
            base = stripKnownTags(name);
        }

        return trim(base) + tag;
    }

    function stripKnownTags(name) {
        var result = trim(name);
        var changed = true;
        var guard = 0;
        while (changed && guard++ < 64) {
            changed = false;
            for (var i = 0; i < knownTags.length; i++) {
                if (endsWithIgnoreCase(result, knownTags[i])) {
                    result = trim(result.substring(0, result.length - knownTags[i].length));
                    changed = true;
                    break;
                }
            }
        }
        return result;
    }

    function stripOneTag(name, tag) {
        var result = trim(name);
        while (endsWithIgnoreCase(result, tag)) {
            result = trim(result.substring(0, result.length - tag.length));
        }
        return result;
    }

    function extractTags(name) {
        var result = trim(name);
        var tags = [];
        var changed = true;
        var guard = 0;
        while (changed && guard++ < 64) {
            changed = false;
            for (var i = 0; i < knownTags.length; i++) {
                if (endsWithIgnoreCase(result, knownTags[i])) {
                    tags.unshift(knownTags[i]);
                    result = trim(result.substring(0, result.length - knownTags[i].length));
                    changed = true;
                    break;
                }
            }
        }
        return tags;
    }

    function endsWithIgnoreCase(value, suffix) {
        value = String(value || "");
        suffix = String(suffix || "");
        if (suffix.length > value.length) return false;
        return value.substring(value.length - suffix.length).toLowerCase() === suffix.toLowerCase();
    }

    function trim(value) {
        return String(value || "").replace(/^\s+|\s+$/g, "");
    }

    function ok(message) {
        return { ok: true, message: message || "OK" };
    }

    function fail(message) {
        return { ok: false, message: message || "Failed" };
    }

    function toJson(value) {
        if (typeof JSON !== "undefined" && JSON.stringify) {
            return JSON.stringify(value);
        }

        if (value === null) return "null";
        if (typeof value === "number" || typeof value === "boolean") return String(value);
        if (typeof value === "string") return quote(value);
        if (value instanceof Array) {
            var arr = [];
            for (var i = 0; i < value.length; i++) arr.push(toJson(value[i]));
            return "[" + arr.join(",") + "]";
        }
        if (typeof value === "object") {
            var props = [];
            for (var key in value) {
                if (value.hasOwnProperty(key)) props.push(quote(key) + ":" + toJson(value[key]));
            }
            return "{" + props.join(",") + "}";
        }

        return quote(String(value));
    }

    function quote(value) {
        return "\"" + String(value)
            .replace(/\\/g, "\\\\")
            .replace(/"/g, "\\\"")
            .replace(/\r/g, "\\r")
            .replace(/\n/g, "\\n")
            .replace(/\t/g, "\\t") + "\"";
    }
})();
