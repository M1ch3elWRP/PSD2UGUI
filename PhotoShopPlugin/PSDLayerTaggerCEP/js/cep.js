(function () {
  "use strict";

  function CSInterface() {}

  CSInterface.prototype.evalScript = function (script, callback) {
    if (!window.__adobe_cep__) {
      if (callback) callback("");
      return;
    }

    window.__adobe_cep__.evalScript(script, callback || function () {});
  };

  CSInterface.prototype.getSystemPath = function (pathType) {
    if (!window.__adobe_cep__) return "";
    return window.__adobe_cep__.getSystemPath(pathType);
  };

  window.CSInterface = CSInterface;
  window.SystemPath = {
    EXTENSION: "extension"
  };
})();
