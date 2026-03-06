# PSDTools 使用说明与技术文档

本文件包含：工具流（流程 + 配置说明）/ 注意事项（Q&A）/ 技术文档（关键技术点与方法说明）。

## 1. 工具流（流程）

### 1.1 Photoshop 导出流程
1) 在 PSD 中按规则给图层或组命名（带 `@` 后缀）。  
2) 运行脚本 `Assets/PSDTools/Editor/PhotoShopScript/ExportToPNG.jsx`（或同步到 PS 脚本目录后在 PS 内运行）。  
3) 导出得到：  
   - PNG 切图  
   - `.ps.data`（包含图层信息的 JSON 数据）

### 1.2 Unity 端新建模式（Create）
1) 打开菜单：`PSDTools/Create UI From PSD`。  
2) 拖入 `.ps.data`，按需指定 `PSDImportConfig`。  
3) 点击 **Create UI**，工具会直接生成以 **Canvas 根节点**为入口的结构。  

### 1.3 Unity 端还原模式（Restore）
1) 打开菜单：`PSDTools/Restore UI From PSD`。  
2) 拖入 `.ps.data`，指定 Target Root（白膜 Prefab 根节点）。  
3) 可视化模式下点击 **智能匹配** 或手动绑定。  
4) 点击 **Apply All (Save)** 写回所有节点属性。  

## 2. 命名后缀与导出规则

以下后缀由导出脚本识别并决定类型/容器行为（部分会影响 Unity 组件与布局行为）：

- `@Img`：普通 Image 图层。  
- `@ImgNoTrim`：Image 图层，按图层原始边界导出（不依赖 Trim）。  
- `@Bg`：Image 图层，导入时不参与 9-Slice 自动切分。  
- `@Btn`：Button 容器。  
- `@H`：Horizontal Layout 容器。  
- `@V`：Vertical Layout 容器。  
- `@G`：Grid Layout 容器。  
- `@Item`：列表 Item 容器/模板。  
- `@Common`：在 Unity 端进行通用图片匹配（详见配置说明）。  

导出脚本选项：
- **Only Export @ Tagged**：勾选后仅导出带 `@` 的图层/组。  

## 3. 配置说明（PSDImportConfig）

配置资产路径：`Assets/PSDTools/Editor/PSDImportConfig.asset`  

### 3.1 匹配评分相关
- `maxDistanceError`：位置允许偏差（像素）。  
- `maxSizeDiff`：尺寸允许偏差（宽高差绝对值之和）。  
- `weightPosition`：位置得分权重。  
- `weightSize`：尺寸得分权重。  
- `weightType`：类型得分权重（Button/Text/Image/RawImage/Layout/Item）。  

### 3.2 调试
- `showDetailedLog`：输出详细匹配日志（Top5 候选、加权分、跳过原因等）。  

### 3.3 匹配过滤
- `skipInactiveMatch`：勾选后，不参与匹配的节点包含 `inactive` 的 Prefab 节点。  

### 3.4 Auto 9-Slice
- `autoSlice`：开启后自动检测大面积重复像素并设置切片。  

### 3.5 资源去重
- `dedupeSprites`：启用内容哈希去重。  
- `dedupeMoveDuplicates`：将重复 PNG 移动到子目录（不删除）。  
- `dedupeMoveFolder`：重复 PNG 目标子目录名。  

### 3.6 通用图片匹配
- `commonSpriteMatch`：启用 `@Common` 匹配。  
- `commonSpriteFolders`：通用图集所在目录（Assets 下路径）。  
- `commonSpritePerceptualThreshold`：感知哈希阈值（0 关闭）。  
- `commonSpriteMoveMatched`：匹配到通用图后，移动导出 PNG 到子目录。  
- `commonSpriteMoveFolder`：匹配成功后的子目录名。  

### 3.7 组件覆盖
- `imageComponent`：Image 覆盖组件（需继承 UnityEngine.UI.Image）。  
- `rawImageComponent`：RawImage 覆盖组件。  
- `buttonComponent`：Button 覆盖组件。  
- `textComponent`：Text 覆盖组件。  

### 3.8 字体覆盖
- `defaultTextFont`：所有 Text 组件统一字体。  

### 3.9 Layout 覆盖
- `horizontalLayoutComponent`：@H 对应 Layout 组件。  
- `verticalLayoutComponent`：@V 对应 Layout 组件。  
- `gridLayoutComponent`：@G 对应 Layout 组件。  

## 4. 注意事项 / Q&A

**Q1：为什么位置或尺寸不对？**  
A：还原逻辑以 PSD 中心坐标为基准换算到目标节点 Anchor 下的 `anchoredPosition`。如果父级有 LayoutGroup 或已做了适配锚点，会影响坐标表现。  

**Q2：隐藏节点是否参与匹配？**  
A：由 `skipInactiveMatch` 控制，勾选后会跳过 `inactive` 节点。  

**Q3：@Bg 为什么不切九宫？**  
A：避免背景图被误切，导入时对 `@Bg` 强制关闭自动切片。  

**Q4：通用图片匹配无效？**  
A：检查 `commonSpriteMatch` 是否开启，`commonSpriteFolders` 是否有效。路径为空或无效会直接禁用并输出一次提示。  

**Q5：重复图片怎么处理？**  
A：`dedupeSprites` 按像素哈希去重；`dedupeMoveDuplicates` 会将重复 PNG 移到指定子目录。  

**Q6：导出 PNG 颜色/内容异常（黑图）？**  
A：脚本会对组或智能对象执行合并/栅格化来避免黑图；如异常继续出现，检查是否为特殊图层效果导致。  

## 5. 技术文档（关键技术点与方法说明）

### 5.1 关键模块
- `ExportToPNG.jsx`：PS 端导出 PNG + `.ps.data`。  
- `PSDLoader`：读取 `.ps.data` 并解析为 `PSDData`。  
- `PSDCreateor`：新建/还原入口，负责节点创建与同步。  
- `PSDImportWorkflow`：Create/Restore 执行编排与参数校验。  
- `PsdCreateWindow`：Create 模式窗口（仅展示与触发）。  
- `VisualBindingRestoreService`：还原模式的匹配与应用服务。  
- `VisualBindingWindow`：Restore 模式窗口（可视化还原与匹配 UI）。  
- `PSDMatchingStrategy`：匹配评分计算与最佳候选查找。  
- `PSDLayoutTool`：LayoutGroup 参数计算与应用。  
- `PSDGroupTool`：空组对齐工具。  
- `PSDAssetDeduper`：资源去重与路径规范化。  
- `PSDCommonSpriteMatcher`：通用图片匹配（@Common）。  
- `PSDNineSliceUtility`：九宫切片检测。  

### 5.2 匹配机制（核心公式）
1) 计算 PSD 中心坐标在 Unity 世界坐标的目标点。  
2) 对每个候选节点计算：  
   - **位置分**：`scorePos`（距离越近分数越高）  
   - **尺寸分**：`scoreSize`（尺寸越接近分数越高）  
   - **类型分**：`scoreType`（类型匹配得 100，否则 0）  
3) 加权合成：  
   - `total = scorePos*weightPosition + scoreSize*weightSize + scoreType*weightType`  
4) 结果排序，取得分最高且未被占用的节点。  

### 5.3 ID 绑定优先级
若存在历史绑定记录，则以 ID 绑定优先，直接锁定目标节点（得分 9999）。  

### 5.4 关键方法注释（入口与影响点）
- `PSDCreateor.CreateUGUI_GenerateMode`：新建模式入口，创建 Canvas 根并逐层生成节点。  
- `PSDCreateor.RefreshNode`：同步节点核心入口，负责尺寸、位置、组件、Layout。  
- `PSDCreateor.ApplyPsdPosition / ApplyPsdPositionLocal`：坐标换算，保持 Anchor 不变。  
- `PSDMatchingStrategy.CalculateMatchScore`：基础评分公式。  
- `VisualBindingWindow.RunAutoMatch`：可视化匹配入口，支持详细日志输出。  
- `PSDCommonSpriteMatcher.TryResolveCommonSprite`：@Common 图像匹配（精确哈希优先，感知哈希兜底）。  
- `PSDCommonSpriteMatcher.MoveMatchedExport`：匹配到通用图后移动导出 PNG。  
- `PSDNineSliceUtility.TryDetectBorder`：自动检测大面积重复像素并设置切片。  

### 5.5 日志与调试
开启 `showDetailedLog` 后，日志将输出：  
- 当前匹配参数  
- 每个 PSD 项的 Top5 候选评分拆解  
- 跳过原因统计（inactive/occupied/root）

### 5.6 匹配流水线与坐标系约定
为避免规则匹配与 ML 特征在“同一输入”下出现口径偏差，当前匹配流水线统一为三步：

1) **统一几何抽取**：
   - Unity 节点使用 `PSDMatchGeometry.ExtractNodeGeom`，在 `root` 的本地坐标系下得到 `centerLocal/sizeLocal/anchor/depth`。
   - PSD 图层使用 `PSDMatchGeometry.BuildPsdGeom`，同样映射到以 PSD 画布中心为原点的本地坐标口径。

2) **统一评分入口**：
   - 规则评分统一走 `PSDMatchScoring.Evaluate`，输出 `ScoreBreakdown`（距离、尺寸差、位置分、尺寸分、类型分、加权总分等）。
   - `PSDMatchingStrategy` 与 `VisualBindingRestoreService` 不再重复维护距离/尺寸公式，仅作为调用方。

3) **统一特征口径（ML）**：
   - `PSDMatchFeatureExtractor` 的 `distNorm/sizeNorm/sameDepth/anchorDiff` 由 `PSDMatchScoring.BuildGeometryBreakdown` 提供，确保与规则路径共享同一几何结果。

### 5.7 编辑器侧静态回归样例
- 回归入口：`PSDTools/Debug/Run Match Scoring Regression`。
- Fixture：`Editor/Fixtures/PSDMatchScoringRegression.fixture.json`。
- 校验目标：同一组几何输入下，以下三条路径输出一致：
  1. `PSDMatchScoring.Evaluate`（统一评分入口）
  2. `PSDMatchingStrategy.CalculateMatchScoreFromGeometry`
  3. `VisualBindingRestoreService.CalculateScoreFromGeometry`
- 同时校验 ML 特征中的几何项（dist/size/depth/anchor）与统一几何拆解一致。

