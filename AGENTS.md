# Repository Guidelines

## Project Structure & Module Organization
The PSD restoration workspace is `D:\project\codesvn_for_import\Assets\_OpenCode\TZUI\PS`; this package lives under `Assets/_OpenCode/TZUI/PS/PSD2NGUI`. Core Unity Editor code is in `Editor/`, including PSD import, restore matching, layout utilities, and debug runners. Machine-learning match helpers and data are in `ML/` and `ML/Editor/`. Photoshop export automation is split between `PhotoShopScript/ExportToPNG.jsx` and the CEP panel in `PhotoShopPlugin/PSDLayerTaggerCEP/`. Match logs are written to `Log/`; avoid treating generated logs as source unless they document a regression. Unity assets such as `.asset`, `.json`, and `.meta` files are part of the package state. The package assumes the host Unity project already imports NGUI; this repo does not bundle NGUI source.

## Build, Test, and Development Commands
`package.json` is a Unity package manifest, not an npm script entry point.

- Unity menu `PSD2NGUI/Create UI From PSD`: open the PSD-to-NGUI creation workflow.
- Unity menu `PSD2NGUI/Restore UI From PSD`: open restore/binding workflow.
- `node --check PhotoShopScript/ExportToPNG.jsx`: syntax-check the Photoshop export script.
- `node --check PhotoShopPlugin/PSDLayerTaggerCEP/js/app.js`: syntax-check CEP panel logic.

After C# changes, refresh assets and force Unity script compilation; verify zero errors and warnings.

## Coding Style & Naming Conventions
Use 4-space indentation for C#. Follow Unity conventions: PascalCase for types and public methods, camelCase for locals and private fields, and keep project classes `PSD`-prefixed (`PSDLoader`, `PSDMatchGeometry`). Keep editor-only code under `Editor/`. Preserve Unity `.meta` files and GUIDs when moving assets. For JSX/CEP JavaScript, follow the existing style and keep Photoshop-specific logic isolated from Unity C#.

## Testing Guidelines
There is no standalone test runner configured. For restore or layout changes, test both scene instances and prefab assets.

## Current Restore Matching Optimizations
As of 2026-04-29, the first- and second-priority matching accuracy work is part of this package.

First-priority restore changes:
- `PSDMatchScoring` is the unified scoring path for restore matching.
- Size scoring uses relative width/height difference with an absolute `maxSizeDiff` fuse. Position scoring uses a soft threshold instead of a hard cliff.
- `sameDepth` and `anchorDiff` now feed weighted score terms through `PSDImportConfig.weightDepth` and `PSDImportConfig.weightAnchor`.
- `passThresholds` is based on geometry signal plus `minAcceptScore`, not just one loose geometry field.
- `PSDMatchTypeUtility` centralizes type compatibility. `UISprite + UIWidget` remains compatible with PSD image layers, and PSD button layers can match `UIButton`, `UIToggle`, `UISlider`, `UIScrollBar`, `UIInput`, `UIPopupList`, or other `UIWidget` nodes with graded scores.
- `PSDTagUtility` and `PhotoShopScript/ExportToPNG.jsx` use exact tokenized `@tag` matching. Do not reintroduce substring checks such as `indexOf("@Btn")`.
- `PSDBindingData` stores paths relative to the restore root, so saved ID history paths should not include the root object name.
- ID history matching is controlled by `PSDImportConfig.enableIdHistoryMatch` and is disabled by default during testing. Enable it only when the binding asset history is trusted.
- `ExportToPNG.jsx` writes `.ps.data` with `toPrettyJson(...)`, producing indented multi-line JSON for manual inspection.

Second-priority restore changes:
- Candidate pruning is controlled by `enableCandidatePruning` and `candidateDistanceMultiplier`; default pruning radius is `maxDistanceError * 2.5`.
- Hierarchy preference uses already matched PSD parents: direct children get `parentAffinityBonus`, descendants get `hierarchyDescendantAffinityBonus`, and nodes outside the matched parent subtree receive `hierarchyOutsideParentPenalty`.
- Pre-Lock is enabled by default. Dynamic Pre-Lock threshold uses `max(matrixMaxScore * preLockDynamicRatio, preLockDynamicMinThreshold)` and also requires `preLockMinScoreGap`.
- Restore bindings track `bestCandidateScore`, `secondBestCandidateScore`, `scoreMargin`, `isLowConfidence`, `skippedSpatialCandidates`, and `hierarchyPenalizedCandidates`.
- The Visual Binding UI shows the top1/top2 margin. Low-confidence matches are marked with `!` and are not auto-confirmed.
- `PSDMatchLogExporter` includes the new matching config fields, pruning counters, hierarchy penalty counters, confidence margin, and low-confidence status.
- `enableGeometryReject` is enabled by default and rejects candidates only when both center distance and relative size error are clearly bad, preventing stale nearby-ish nodes from winning by type or history.
- `@ScrollRect` maps a PSD group to a scroll-list root. Create mode generates a normal `UIScrollView/Viewport/Content` structure; restore mode matches existing `UIScrollView` roots and routes PSD list children to `Content`.
- Image reuse runs after target PSD size is known. Default mode exact-reuses local duplicates; existing atlas sprite border wins over auto 9-slice.
- When a matched child is controlled by a parent `UITable`/`UIGrid`, Apply can sync the PSD skeleton parent rect onto the Unity parent container before restoring the child. This fixes Award-like group offset/size drift.

ML matching is still intentionally disabled in `VisualBindingRestoreService`; do not re-enable it unless the user explicitly asks for the third-priority ML phase.

## Commit & Pull Request Guidelines
Git history was not inspected in this environment. Use concise imperative commits such as `fix: restore prefab matching geometry` or `docs: update PSD2NGUI guide`. PRs should include the Unity version, affected workflow, reproduction steps, validation performed, and screenshots or GIFs for UI/layout changes.

## Agent-Specific Notes
Do not sync scripts into a local Photoshop installation unless explicitly requested. In restricted environments, avoid repeated git/network calls and use Unity MCP compilation checks when available.
