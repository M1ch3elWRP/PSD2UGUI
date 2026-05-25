# Repository Guidelines

## Project Structure & Module Organization
The PSD restoration workspace is `D:\project\codesvn_for_import\Assets\_OpenCode\TZUI\PS`; this package lives under `Assets/_OpenCode/TZUI/PS/PSDTools`. Core Unity Editor code is in `Editor/`, including PSD import, restore matching, layout utilities, and debug runners. Machine-learning match helpers and data are in `ML/` and `ML/Editor/`. Photoshop export automation is split between `PhotoShopScript/ExportToPNG.jsx` and the CEP panel in `PhotoShopPlugin/PSDLayerTaggerCEP/`. Match logs are written to `Log/`; avoid treating generated logs as source unless they document a regression. Unity assets such as `.asset`, `.json`, and `.meta` files are part of the package state.

## Build, Test, and Development Commands
`package.json` is a Unity package manifest, not an npm script entry point.

- Unity menu `PSDTools/Create UI From PSD`: open the PSD-to-UGUI creation workflow.
- Unity menu `PSDTools/Restore UI From PSD`: open restore/binding workflow.
- Unity menu `PSDTools/Debug/Run Match Scoring Regression`: run scoring regression fixture checks.
- `node --check PhotoShopScript/ExportToPNG.jsx`: syntax-check the Photoshop export script.
- `node --check PhotoShopPlugin/PSDLayerTaggerCEP/js/app.js`: syntax-check CEP panel logic.

After C# changes, refresh assets and force Unity script compilation; verify zero errors and warnings.

## Coding Style & Naming Conventions
Use 4-space indentation for C#. Follow Unity conventions: PascalCase for types and public methods, camelCase for locals and private fields, and keep project classes `PSD`-prefixed (`PSDLoader`, `PSDMatchGeometry`). Keep editor-only code under `Editor/`. Preserve Unity `.meta` files and GUIDs when moving assets. For JSX/CEP JavaScript, follow the existing style and keep Photoshop-specific logic isolated from Unity C#.

## Testing Guidelines
There is no standalone test runner configured. Prefer targeted Unity menu smoke tests in `PSDTools/Debug/` and regression fixtures under `Editor/Fixtures/`. For restore or layout changes, test both scene instances and prefab assets, then inspect generated match logs only when needed.

For agent-driven restore debugging, use `PSDTools/Agent/Run Restore Audit` or `PSDTools/Debug/Run Match Audit Smoke`. The smoke test should log `[MatchAuditSmoke] PASS` and export an audit package under `Log/Audit/`.

## Current Restore Matching Optimizations
As of 2026-04-29, the first- and second-priority matching accuracy work plus the current audit/debug safeguards are part of this package.

First-priority restore changes:
- `PSDMatchScoring` is the unified scoring path for restore matching and regression checks.
- Size scoring uses relative width/height difference with an absolute `maxSizeDiff` fuse. Position scoring uses a soft threshold instead of a hard cliff.
- `sameDepth` and `anchorDiff` now feed weighted score terms through `PSDImportConfig.weightDepth` and `PSDImportConfig.weightAnchor`.
- `passThresholds` is based on geometry signal plus `minAcceptScore`, not just one loose geometry field.
- `PSDMatchTypeUtility` centralizes type compatibility. `Image + Selectable` remains compatible with PSD image layers, and PSD button layers can match `Button`, `Toggle`, `Slider`, `Scrollbar`, `InputField`, `Dropdown`, or other `Selectable` nodes with graded scores.
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
- `@StdBtn` reuse now detects strict NormalBtn candidates, `UIStdButton` prefab roots, and prefab roots under `/CommonPrefbs/Btn/`. Candidate diagnostics are exported as `stdPrefabCandidates`.
- `@PopUp` is a standard popup panel prefab root. It only considers `Pnl_Win00` through `Pnl_Win09` under `/CommonPrefbs/Panel/`, using the built-in visible-size table for panel selection and reuse scoring.
- `@ScrollRect` maps a PSD group to a scroll-list root. Create mode generates a normal `ScrollRect/Viewport/Content` structure; restore mode matches existing `ScrollRect` or `TZ.UI.VirtualScrollRect` roots and routes PSD list children to `Content`.
- Image reuse runs after target PSD size is known. Default mode exact-reuses local duplicates and common sprites under `UITextures/Common`, `Common2`, `Panel`, and `Panel2`; `@CommonSprite` raises priority for project common sprite matching. `@CommonSpriteWhite` matches white tintable sprites from `commonSpriteWhiteFolders` by alpha shape and applies the PSD color through `Image.color`. Existing common sprite `spriteBorder` wins over auto 9-slice.
- Popup visible sizes: `Pnl_Win00 1004x642`, `Pnl_Win01 1216x710`, `Pnl_Win02 982x640`, `Pnl_Win03 982x640`, `Pnl_Win04 480x590`, `Pnl_Win05 608x396`, `Pnl_Win06 900.41x590`, `Pnl_Win07 618x626`, `Pnl_Win08 900.41x590`, `Pnl_Win09 1144x680`.
- When a matched child is controlled by a parent `LayoutGroup`, Apply can sync the PSD skeleton parent rect onto the Unity parent container before restoring the child. This fixes Award-like group offset/size drift.

ML matching is still intentionally disabled in `VisualBindingRestoreService`; do not re-enable it unless the user explicitly asks for the third-priority ML phase.

## Agent Restore Audit Workflow
As of 2026-04-29, the package includes an agent-oriented audit chain for identifying wrong restore matches.

- `PSDMatchAuditConfig` is the configurable entrypoint. Default asset path: `Assets/_OpenCode/TZUI/PS/PSDTools/Editor/PSDMatchAuditConfig.asset`.
- `PSDTools/Agent/Create Default Audit Config` creates/selects the default config.
- `PSDTools/Agent/Run Restore Audit` runs matching from the config. In DryRun it also simulates Apply on loaded prefab contents without saving assets, so layout/prefab side effects are visible in the audit image.
- Audit packages export `audit_summary.json`, `all_layers.json`, `suspects.json`, `agent_review_template.json`, `template_psd.png` when a template exists, `layer_composite.png`, `full_overlay.png`, and cropped `suspects/*.png`.
- `VisualBindingWindow` also has `导出审计包`, which exports from the current loaded binding state.
- Audit truth uses `template_psd.png` when available. `layer_composite.png` is only the reconstructed exported-layer view and can miss text, common prefab visuals, and scene-only content.
- Audit images use blue for PSD rects, orange for matched Unity rects, and red for suspect rects. Candidate colors are recorded in JSON.
- Suspect detection combines match state and visual geometry: unmatched, auto-created/new-node, low confidence, poor IoU, large center/size error, displaced Hungarian choice, hierarchy outside parent, spatial pruning, geometry rejection, StdPrefab failures, and duplicate ambiguity.
- `suspects.json` intentionally focuses on problems. Read `all_layers.json` when investigating a successful match that still behaved unexpectedly, such as `@StdBtn` reuse or geometry source selection.
- `all_layers.json` image entries include `imageReuse` diagnostics: source kind, selected sprite path, canonical export path, confidence, slice border, and rejection reason.
- The intended agent loop is: run audit, inspect `audit_summary.json` and `all_layers.json`, inspect suspect PNGs visually, fill/update `agent_review_template.json` verdicts, identify cause tags, patch scoring/type/hierarchy/export/apply logic, rerun audit and regression.

## Commit & Pull Request Guidelines
Git history was not inspected in this environment. Use concise imperative commits such as `fix: restore prefab matching geometry` or `docs: update PSDTools guide`. PRs should include the Unity version, affected workflow, reproduction steps, validation performed, and screenshots or GIFs for UI/layout changes.

## Agent-Specific Notes
Do not sync scripts into a local Photoshop installation unless explicitly requested. In restricted environments, avoid repeated git/network calls and use Unity MCP compilation checks when available.
