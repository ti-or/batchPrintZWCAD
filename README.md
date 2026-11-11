# LA批量打印（纸质打印修改版）

基于 [Edwardhehe/batchPrintZWCAD](https://github.com/Edwardhehe/batchPrintZWCAD) 修改的 ZWCAD / AutoCAD 批量打印插件。
原版只能输出 PDF、PNG、JPG、DWF 或按图框拆分 DWG；本版新增**直接打印到纸质**——把图框送进系统打印机/绘图仪，不再先生成电子文件。

> **本修改版由 AI 完成**（DeepSeek Harness 编码智能体），未经原作者审核，与原项目无隶属关系。
> 使用问题请在本 fork 反馈，不要打扰原作者。

## 与原版的差异

| 项目 | 原版 v1.15.10.1 | 本版 v1.16.0 |
| --- | --- | --- |
| 输出格式 | PDF / PNG / JPG / DWF / DWG | 增加 **纸质**（直接送打印机） |
| 打印机 | 无 | 可选本机/网络打印机、绘图仪 |
| 份数 | 无 | 1–99，整批统一 |
| 纸张匹配 | 插件自有 `LA_pdf`/`LA_dwf` 纸型 | 按打印机驱动纸型匹配图幅（3 mm 容差）；缺纸默认停止并列出可用纸张，可勾选「纸张不匹配时改用最接近的纸张」 |
| 周边留白 / 合并 PDF | 支持 | 纸质模式下不可用（系统打印机无法注册插件自定义纸） |
| 单张打印 | 输出 PDF | 仍输出 PDF（需要纸质时用批量窗口，只勾一张） |

支持纸质的窗口：**图框库批量打印**、**通用型批量打印**；两处的「预览」在纸质模式下仍可用，预览效果与实际送打印机一致。

## 安装（ZWCAD）

1. 下载 `LA批打印-ZWCAD-v1.16.0.zip`，解压到固定目录（不要删除或移动）。
2. 中望 CAD 命令行输入 `NETLOAD`，选择目录中的 `BatchPlotter.dll`。
3. 菜单「批量打印」→「安装自动加载」，以后启动自动加载。

> 本 fork 目前只提供 ZWCAD 版编译产物。AutoCAD 2015–2024 用 `AcadBatchPlot.dll`、2025–2027 用 `AcadBatchPlot.Core.dll`，需按下面的编译方式自行构建。

## 纸质打印使用方法

1. 打开「批量打印」或「通用型批量打印」窗口，输出格式选 **纸质**。
2. 选择打印机，设置份数（1–99）。
3. 点「开始打印」。每张图按清单顺序送打印引擎；完成后提示已送打印机多少张。

缺纸时：默认停止并列出打印机可用纸张；也可勾选「纸张不匹配时改用最接近的纸张」自动改纸（按长宽比与尺寸选最近的纸）。

## 编译

```powershell
dotnet build src\BatchPlotter\BatchPlotter.csproj -c Release   # → bin\BatchPlotter.dll
dotnet build LA.BatchPlot.sln -c Release                       # 三个平台一起编
```

需要 .NET SDK 9.0.1xx（仓库 `global.json` 要求 9.0.100）；AutoCAD 2025–2027 版本还需 .NET 8 运行时。

## 许可证

MIT，遵循原项目。
