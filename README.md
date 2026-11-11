# Semiconductor Limit Editor

STDF / CSV 阈值修改与 DUT 重新分 BIN 的 Windows WPF 工具，基于 Semiconductor Data Analyzer 2.0.12 的独立改造。原分析器仓库保持独立，基线提交 `fa93b549c0b4a04b3daf21e5c53d8d55c923d76f` 的历史与 MIT 声明保留。

直接使用分析器主界面：新增新 LSL、新 USL、良率变化三列；冻结原始良品分类，按参与测项逐条重判，在新文件内同步受影响 DUT 的 SBIN/HBIN。支持 CSV 原文保留、STDF 原记录流写出、大小端/gzip、计数同步及规则/变更清单。

[使用说明与发行边界](Docs/ReleaseNotes-3.0.0.md) · [完整方案](Docs/RejudgePlan.md) · [原分析器基线说明](BASELINE_README.md)

## 下载与运行

在 [Releases](https://github.com/ttt3t/semiconductor-limit-editor/releases) 下载 `SemiconductorLimitEditor-3.0.0-win-x64-framework-dependent.zip`。安装 **.NET 8 Windows Desktop Runtime x64**，解压后运行 `SemiconductorLimitEditor.exe`。设置保存在独立的 `%LOCALAPPDATA%\SemiconductorLimitEditor`，不使用旧分析器的产品目录。

## 开发与验证

使用 .NET 8 SDK。Windows：

```powershell
dotnet build SemiconductorCsvAnalyzer.csproj -c Release
dotnet run --project SemiconductorCsvAnalyzer.csproj -c Release
```

Linux 可以交叉编译（加 `-p:EnableWindowsTargeting=true`），并运行可移植数据回归：

```bash
bash Tests/run-rejudge.sh
bash Tests/run-core-memory.sh
bash Tests/run-good-retest.sh
bash Tests/run-import-progress.sh
bash Tests/run-stdf-numbering.sh
bash Tests/run-scatter-raster.sh
bash Tests/run-column-search.sh
bash Tests/run-map-table-copy.sh
```

`run-rejudge.sh` 使用生成的合成样本，检查分类/参与、双 BIN、待确认、复测、CSV 原文及 STDF 大小端/gzip、未知扩展、继承限值、PRR 与各计数；不提交生产数据。Windows 发布工作流额外运行真实 WPF 导入/编辑/预览 smoke test，成功后才发布标签对应 Release。

```powershell
dotnet publish SemiconductorCsvAnalyzer.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o artifacts/win-x64-framework-dependent
```

产物为依赖框架的 EXE，不是自包含运行时的发行包。程序使用 ClosedXML，无需 Excel。
