# PSD Layer Tagger CEP

Photoshop 2020 CEP panel for quickly adding PSD2NGUI suffix tags to selected layers.

## Why CEP

ScriptUI dialogs can block layer selection or become unstable when used as a floating hierarchy picker. CEP runs as a dockable Photoshop panel, so artists can keep selecting layers normally and click tag buttons when needed.

## Install

Run this PowerShell script:

```powershell
powershell -ExecutionPolicy Bypass -File ".\Install-PSDLayerTaggerCEP.ps1"
```

Then restart Photoshop and open:

```text
Window > Extensions > PSD Layer Tagger
```

The installer copies the extension to:

```text
%APPDATA%\Adobe\CEP\extensions\com.tzui.psdtools.layer-tagger
```

It also enables unsigned CEP extensions in HKCU for CSXS 9/10/11.

## Tags

- `@StdBtn`: NormalBtn common button.
- `@PopUp`: common popup panel (`Pnl_Win00` through `Pnl_Win09`).
- `@ItemBox`: square common item.
- `@ItemCircle`: circular common item.
- `@ScrollRect`: scrollable list root. Combine with `@H`, `@V`, or `@G` for content layout.
- `@Item`: non-standard item container.
- `@Img`: normal image.
- `@CommonSprite`: regular project common sprite reuse.
- `@CommonSpriteWhite`: white tintable common sprite reuse.
- `@ImgNoTrim`: image using layer bounds.
- `@Btn`: normal button container.
- `@H`: horizontal layout.
- `@V`: vertical layout.
- `@G`: grid layout.

## Notes

- The panel renames currently selected Photoshop layers via Action Manager by layer ID.
- Multi-select is supported.
- Default mode replaces existing known PSD2NGUI tags at the end of the layer name.
- Append mode only removes the same tag before appending it.
