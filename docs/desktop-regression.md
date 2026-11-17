# 桌面回归与绘制异常记录

更新日期：2026-11-17。适用于源码开发和回归验证，日常使用仍运行 `WordFinder.exe`。

## 怎么运行

在 Windows 上安装项目要求的 .NET SDK 10，从仓库根目录执行：

```powershell
dotnet build WordFinder.sln -c Release
dotnet test tests/WordFinder.Tests -c Release
dotnet run --project tools/WordFinder.Benchmarks -c Release --no-build -- ui-regression artifacts/desktop-tests
```

最后一条命令使用真实的 `MainForm`，但隐藏测试窗口。它会在指定路径下新建唯一的 `ui-regression/<随机标识>` 子目录，生成一个 150 行的工作簿和独立索引，不读取日常索引或办公文件。生成的数据留在本地，`artifacts/` 不会提交 Git。

成功时退出码为 0，并输出一行 JSON，其中 `Passed`、`CrossThreadChecks`、`EmptyResults`、`CancelledClose` 都为 `true`，`Rounds` 为 10。`Paints` 和 `Bindings` 是实际绘制、绑定回调次数，会随运行情况变化；`UiThread` 是本次测试的线程编号。

单次包含以下检查：

- 首屏为第 1 页、1–50 行，分页历史为空。
- 连续点击两次“下一页”只前进一页，显示 51–100 行，历史只增加一次。
- 取消下一次翻页后保留原结果及历史，再点击“上一页”能返回第 1 页。此组合重复 10 轮。
- 无匹配结果不残留旧行；清空目标后清除旧数据源。
- 查询等待期间关闭窗口，取消查询后不再访问已释放控件。
- 绘制和数据绑定均发生在同一 UI 线程，整个异步检查保持 WinForms 同步上下文。

需要像本轮一样做 5 次独立运行时，在构建完成后执行：

```powershell
1..5 | ForEach-Object {
    dotnet run --project tools/WordFinder.Benchmarks -c Release --no-build -- ui-regression artifacts/desktop-tests
    if ($LASTEXITCODE -ne 0) { throw "第 $_ 次桌面回归失败。" }
}
```

这会累计检查 50 轮翻页。命令失败时保留终端输出，连同 Windows 版本、运行时版本和操作步骤提交问题；不要上传包含真实办公内容的索引。

## 这次异常怎么定位的

收到的堆栈是在 `DataGridViewTextBoxCell.PaintPrivate` 中抛出空引用。日志显示，入口程序集来自本地 `artifacts/review` 中的临时审查程序 `WordFinder.Benchmarks`。

旧审查程序通过 `Application.DoEvents()` 循环等待异步任务。临时最外层消息循环结束后，线程的 WinForms 同步上下文会被卸载；后续翻页的异步续体可能在线程池更新 `DataGridView.DataSource`，与 UI 绘制并发。相关循环行为可对照 [WinForms 上游源码](https://github.com/dotnet/winforms/blob/v10.0.0/src/System.Windows.Forms/System/Windows/Forms/Application.ThreadContext.cs)。

在同一入口开启 `Control.CheckForIllegalCrossThreadCalls` 后，稳定捕获到线程池调用 `MainForm.SearchAsync` 更新表格的跨线程异常。根据入口和绘制堆栈，判断这处并发访问是附件异常的原因。**本轮独立复现了跨线程更新，没有复现完全相同的 `PaintPrivate` 堆栈。**

## 修复方式

桌面检查集中在 `tools/WordFinder.Benchmarks/DesktopChecks.cs`：

- 用专属 STA 线程和持续的 `Application.Run(ApplicationContext)` 运行窗口及异步检查，取消手动 `DoEvents` 等待。
- 开启跨线程访问检查，并检查绘制、绑定回调的线程。
- 由测试管理消息循环的生命周期。窗口关闭后，继续处理待取消查询的续体，全部断言完成才退出，防止提前结束误报通过。
- 异常传回命令行入口，或在超时后使进程失败。

本机旧审查入口也已转用这套实现。仓库中的 `ui-regression` 命令包含所需代码，其他开发者无需取得被忽略的临时审查目录。

## 复测结果与边界

2026-11-17 在 Windows 11 x64、.NET SDK 10.0.401、运行时 10.0.15 下完成：

| 检查 | 结果 |
| --- | --- |
| Release 解决方案构建 | 0 警告、0 错误 |
| 集成测试 | 20/20 通过 |
| 仓库 `ui-regression` 入口 | 单次 10 轮及全部附加断言通过 |
| 修正后的原审查入口 | 5 个独立进程、共 50 轮通过 |
| 上述 5 次运行的表格绘制回调 | 共 10,535 次，均在 UI 线程 |

这里检查的是隔离数据下的真实桌面控件，不包含 Windows 10 实机、实际办公文件、Excel COM 跳转、文件浏览对话框或杀毒软件验收。历史规模测试另见 [实现与验证](implementation-and-validation.md)。

本机复现和复测记录保存在 `artifacts/paint-fix`，包含 `baseline-strict.log`、`build.log`、`tests.log`、`tool-entry.json` 和 `repeated-checks.json`。这些是本地证据位置，不是仓库中的下载链接。
