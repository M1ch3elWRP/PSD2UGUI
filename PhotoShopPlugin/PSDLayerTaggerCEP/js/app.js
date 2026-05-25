(function () {
  "use strict";

  var cs = new CSInterface();
  var lastSelection = [];

  var tagGroups = {
    stdTags: [
      { tag: "@StdBtn", title: "Std Button", desc: "NormalBtn common button" },
      { tag: "@PopUp", title: "Popup Panel", desc: "Common popup panel" },
      { tag: "@ItemBox", title: "Item Box", desc: "Square common item" },
      { tag: "@ItemCircle", title: "Item Circle", desc: "Circle common item" },
      { tag: "@ScrollRect", title: "Scroll Rect", desc: "Scrollable list root" },
      { tag: "@Item", title: "Item", desc: "Custom item container" }
    ],
    basicTags: [
      { tag: "@Img", title: "Image", desc: "Image" },
      { tag: "@CommonSprite", title: "Common Sprite", desc: "Reuse common sprite" },
      { tag: "@CommonSpriteWhite", title: "White Sprite", desc: "Reuse white sprite with tint" },
      { tag: "@ImgNoTrim", title: "No Trim", desc: "Keep layer bounds" },
      { tag: "@Btn", title: "Button", desc: "Button container" }
    ],
    layoutTags: [
      { tag: "@H", title: "Horizontal", desc: "Horizontal layout" },
      { tag: "@V", title: "Vertical", desc: "Vertical layout" },
      { tag: "@G", title: "Grid", desc: "Grid layout" }
    ]
  };

  function init() {
    Object.keys(tagGroups).forEach(function (id) {
      var root = document.getElementById(id);
      tagGroups[id].forEach(function (item) {
        root.appendChild(createTagButton(item));
      });
    });

    document.getElementById("refreshBtn").addEventListener("click", refresh);
    document.getElementById("clearBtn").addEventListener("click", clearTags);
    document.getElementById("copyBtn").addEventListener("click", copyLayerNames);
    document.addEventListener("keydown", handleHotkeys);

    refresh();
  }

  function createTagButton(item) {
    var btn = document.createElement("button");
    btn.className = "tag-btn";
    btn.innerHTML = "<strong>" + item.tag + "</strong><span>" + item.desc + "</span>";
    btn.title = item.title + " " + item.tag;
    btn.addEventListener("click", function () {
      applyTag(item.tag);
    });
    return btn;
  }

  function getMode() {
    var checked = document.querySelector("input[name=tagMode]:checked");
    return checked ? checked.value : "set";
  }

  function callHost(expression) {
    return new Promise(function (resolve) {
      cs.evalScript(expression, function (raw) {
        resolve(parseHostResult(raw));
      });
    });
  }

  function parseHostResult(raw) {
    if (!raw) {
      return { ok: false, message: "Photoshop returned no result" };
    }

    try {
      return JSON.parse(raw);
    } catch (e) {
      return { ok: false, message: raw };
    }
  }

  function hostString(value) {
    return JSON.stringify(String(value || ""));
  }

  function applyTag(tag) {
    setStatus("Applying " + tag + " ...", true);
    callHost("$._PSDLayerTagger.applyTag(" + hostString(tag) + "," + hostString(getMode()) + ")")
      .then(function (result) {
        renderResult(result);
        refresh();
      });
  }

  function clearTags() {
    setStatus("Clearing tags ...", true);
    callHost("$._PSDLayerTagger.clearTags()").then(function (result) {
      renderResult(result);
      refresh();
    });
  }

  function refresh() {
    callHost("$._PSDLayerTagger.getSelectionInfo()").then(function (result) {
      renderSelection(result);
    });
  }

  function renderResult(result) {
    if (!result || !result.ok) {
      setStatus(result && result.message ? result.message : "Operation failed", false);
      return;
    }

    setStatus(result.message || "Done", true);
  }

  function renderSelection(result) {
    var list = document.getElementById("layerList");
    if (!result || !result.ok) {
      lastSelection = [];
      setStatus(result && result.message ? result.message : "Photoshop is not connected", false);
      list.textContent = "";
      return;
    }

    lastSelection = result.layers || [];
    setStatus("Selected " + result.count + " layers", true);
    list.textContent = lastSelection.map(function (layer) {
      var tagText = layer.tags && layer.tags.length ? "  [" + layer.tags.join(" ") + "]" : "";
      return layer.name + tagText;
    }).join("\n");
  }

  function setStatus(text, ok) {
    var el = document.getElementById("statusText");
    el.textContent = text;
    el.style.color = ok ? "var(--accent-2)" : "var(--danger)";
  }

  function copyLayerNames() {
    if (!lastSelection.length) {
      setStatus("No layer names to copy", false);
      return;
    }

    var text = lastSelection.map(function (layer) { return layer.name; }).join("\n");
    var area = document.createElement("textarea");
    area.value = text;
    document.body.appendChild(area);
    area.select();
    document.execCommand("copy");
    document.body.removeChild(area);
    setStatus("Copied " + lastSelection.length + " layer names", true);
  }

  function handleHotkeys(event) {
    if (event.target && /input|textarea/i.test(event.target.tagName)) return;

    var key = String(event.key || "").toLowerCase();
    var map = {
      "1": "@Img",
      "2": "@Btn",
      "3": "@StdBtn",
      "4": "@PopUp",
      "5": "@ItemBox",
      "6": "@ItemCircle",
      "s": "@ScrollRect",
      "h": "@H",
      "v": "@V",
      "g": "@G"
    };

    if (!map[key]) return;
    event.preventDefault();
    applyTag(map[key]);
  }

  document.addEventListener("DOMContentLoaded", init);
})();
