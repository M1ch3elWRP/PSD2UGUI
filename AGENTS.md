# Repository Guidelines

## Project Structure & Module Organization
This Unity project includes the PSD2Unity editor tooling in `Assets/PSDTools/`. Core editor scripts live in `Assets/PSDTools/Editor/` and are compiled only for the Unity Editor via `PSD2Unity.asmdef`. Photoshop export tooling is located at `Assets/PSDTools/Editor/PhotoShopScript/ExportToPNG.jsx` and generates `.ps.data` metadata plus PNGs. Vendor binaries (Newtonsoft JSON) are in `Assets/PSDTools/Editor/NewtonsoftJson/` and should remain untouched. Project-level content such as `Assets/Images/`, `Assets/Prefab/`, and `Assets/Scenes/` is used for generated assets and UI output.

## Build, Test, and Development Commands
There are no build or test scripts in `package.json` (it is a Unity package manifest). Use the Unity Editor:
- Open create mode: `PSDTools/Create UI From PSD`.
- Open restore mode: `PSDTools/Restore UI From PSD`.
If you must automate, run Unity in batch mode from the project root with `-projectPath`, but no specific CLI entrypoints are defined here.

## Coding Style & Naming Conventions
Follow Unity C# conventions: 4‑space indentation, PascalCase for public types/methods, camelCase for locals/fields, and keep `PSD`-prefixed class names consistent (e.g., `PSDLoader`, `PSDCreateor`). Keep `.meta` files in sync with assets; do not regenerate GUIDs.

## Testing Guidelines
No automated tests are present in this repo. Validate changes manually in the Unity Editor by importing a `.ps.data` file and confirming UI generation. If adding tests, use Unity Test Runner and place them under a `Tests` folder (e.g., `Assets/PSDTools/Tests`).

## Commit & Pull Request Guidelines
This checkout does not include a `.git` directory, so no commit conventions can be inferred. Use your team’s standard (e.g., `feat: ...`, `fix: ...`) and keep messages scoped to editor tooling changes. PRs should include: a short description, Unity version used, steps to reproduce, and screenshots/GIFs for UI or layout changes. Link related issues and call out any exported assets or config changes.

## Configuration & Asset Tips
`ExportToPNG.jsx` defaults to writing into `C:/Images/`; update the save path before exporting. Avoid committing generated PNGs unless they are part of the deliverable. Use `PSDImportConfig.asset` for shared defaults and document any project-specific overrides in the PR.
