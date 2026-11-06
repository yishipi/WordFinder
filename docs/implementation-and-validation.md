# WordFinder 0.1.0 实现与验证

验证日期：2026-11-06。当前程序已经可运行，交付为自包含 Windows x64 单 EXE。本报告区分已实现、已实测与仍需目标机验收的内容。

## 实现结构

`WinForms UI → 查询服务 → SQLite`，搜索不访问源文件。索引由同一个 EXE 的内部工作模式启动：`UI → IndexerController → Windows Job Object → 独立 ExcelDataReader 工作进程 → SQLite`。没有 Excel COM 自动化、Python、Java 或常驻更新服务。

采用 .NET 10.0.14、ExcelDataReader 3.9.0、ExcelNumberFormat 1.2.0、Microsoft.Data.Sqlite 10.0.14、SQLitePCLRaw 2.1.13。WinForms 作为简单原生控件界面实现，尚未制作同等 WPF 版本进行框架对照测试，不能据此声称在所有情况下都更快。

解析使用 Reader 的 `SinglePassMode`，逐行写入小批量事务，不构造 DataSet 或整个工作簿对象。原文件使用只读 FileStream。实际行号取 Reader.Depth，空白行不会压缩坐标。隐藏内容照常读取；公式只取缓存值，数字和日期参考文件保存格式。

数据库按“文件 → 成功版本 → 工作表 → 非空行 → 单元格引用”存储。原文 SHA-256 去重，长文本满足收益阈值时 Brotli 压缩。倒排按 Unicode 字符和相邻双字符生成安全 ASCII token，支持中文单字、编号、标点和字面空格；FTS5 使用无正文、无位置信息配置，另存一份可预览的去重文本。

每个查询目标先通过倒排召回、对去重文本校验完整字面包含，再组合到同一行。SQL 的 materialized CTE 避免高重复值在每个单元格重新解压校验。全部目标同时满足后再分页，每页50行，使用行 ID 游标。搜索防抖250毫秒、取消旧请求，并通过 sqlite3_interrupt 中断旧 SQL。

增量扫描按大小和 UTC 修改时间判断。更新文件先写新版本，完整成功后切换 active generation；失败和取消清理半成品并保留旧成功版本。扫描目录遇到权限/访问问题时保留旧关联，避免把扫描不全误判为全部删除。重叠目录共享文件记录；分类移除及文件删除后回收无引用文本。

内存额度使用 Windows Job Object 的进程提交内存限制，默认512 MiB，可配置256–32768 MiB。不是工作集限制，也不包含 UI、系统文件缓存和其他软件。工作进程收到父进程“go”前不开始索引；父进程先完成 Job 分配及低优先级设置。进程异常结束时记录当前失败文件，重新启动工作进程继续其他文件，避免反复卡在同一文件。

## 已执行检查

- Release 构建：0警告、0错误；10项核心集成测试全部通过。
- 核心测试：同行不同列组合及真实坐标、隐藏内容/保存公式结果/数字格式、原文件 SHA-256 不变、中文单字/Unicode/符号/空格/英文大小写、移除源文件后仍可检索、增量跳过/修改/删除、失败保留旧版及重试、多分类重叠、分页、去重压缩/整理清空、取消后的恢复。
- 官方 ExcelDataReader 测试文件 `10x10.xls`、`10x10.xlsb`、`10x10.xlsx`：3文件均完成索引，查询可命中各格式并保持坐标。来源为 [上游 TestData](https://github.com/ExcelDataReader/ExcelDataReader/tree/develop/src/TestData)，只下载至忽略的测试工件目录，未作为发布样本分发。首版也接受 xlsm 扩展名，尚未针对真实含宏工作簿单独验收；索引不执行宏。
- 实际发布 EXE 的独立工作进程：暂停后计数保持不动、继续恢复、停止后未发布半成品文件，检查通过。
- 发布 EXE 界面已验证：添加本机目录、查看512 MiB额度、更新索引（2成功/1故意损坏失败）、两目标搜索返回4行、实际坐标与隐藏工作表、整行预览、复制整行成功提示、最大化布局。界面测试使用独立数据目录，不访问用户办公文件。
- 最终默认窗口再次核对了紧凑整行预览、分页旁的文件/行数/体积统计及底部搜索状态。目录添加通过粘贴路径方式完成；系统浏览选择对话框尚未完成端到端验收。
- 256 MiB 限额的共享字符串压力样本：大文件触发内存不足，被记录为失败；随后的小文件成功索引。Job 记录峰值256 MiB，恢复后搜索可命中小文件。
- NuGet 漏洞查询在当日配置源中未发现直接或传递依赖的已知漏洞。此项不等于对程序的安全认证。

## 规模实测

主机：Windows 11 x64 build26200，Intel Core Ultra 5 235U（12核/14逻辑处理器），约32 GiB RAM，KIOXIA NVMe SSD。版本为 Release，自包含压缩 EXE。部分测试同时运行构建或界面验证，未隔离所有办公负载。未清空系统文件缓存；“首次查询”指本次查询进程第一次调用，不能视为物理磁盘冷读。

查询固定为两个目标“张三”和“上海”，首屏50行。表中计时来自查询服务，不包含 UI 的250毫秒防抖。每组查询10次，只报告首个与后9次范围，不能从10次样本推导可靠的 P95。

| 样本 | 首次索引 | 工作进程峰值提交内存 | 索引含 WAL | 首次查询 / 后9次 |
| --- | ---: | ---: | ---: | --- |
| 10,000个小xlsx，总约18.1 MiB，1万行/3万单元格 | 143.96秒，10000成功/0失败 | 107.53 MiB | 6.44 MiB | 51.35 ms / 25.23–37.33 ms |
| 500 MiB xlsx，121548行/364644单元格 | 15.68秒，1成功/0失败 | 91.92 MiB | 26.02 MiB | 82.19 ms / 31.84–40.86 ms |
| 上述1万个文件未变化再次更新 | 9.74秒，解析0/跳过10000 | 98.83 MiB | 6.44 MiB | 43.73 ms / 19.48–29.72 ms |

500 MiB 样本实际文件长524290519字节，主体是未压缩的真实 worksheet XML，包含重复长文本、独立编号、稀疏列。索引实读全部工作表内容，没有用图片或无关填充凑文件体积。它属于高重复分布，**不代表500 MiB高熵文本或大量共享字符串的文件也能达到相同比例与内存**。一万个文件测试是小文件数量测试，不是“一万个500 MiB文件”容量测试。

压缩分发有开销：普通未捆绑构建在同一样本的索引进程峰值约39–52 MiB，自包含压缩 EXE 约92–108 MiB。默认512 MiB仍有余量，但内存额度不足时并不保证完成任意500 MiB文件。ExcelDataReader 的共享字符串表会缓存于内存，这一限制已通过失败恢复测试实际观察到。

原始 JSON 报告见 [validation](validation)，其中 `many-first.json`、`large-first.json`、`many-unchanged.json` 和 `memory-recovery.json` 来自发布 EXE；带 `development` 的报告来自未捆绑开发构建。首次索引测试已保留单独报告，避免增量复测覆盖首次数据。

## 发布与更新

SDK 单文件、SelfContained、win-x64、原生依赖解包、EnableCompressionInSingleFile=true；关闭裁剪、Native AOT 和 ReadyToRun。最终压缩EXE约50.55 MiB，未压缩对照构建118829049字节（113.32 MiB），约减少55.4%。未使用第三方壳。精确字节和SHA-256见 validation/build.json。规模测试在最后几项仅调整预览区及状态标签前的相同索引引擎构建上完成；核心测试在本轮发布流程中再次全部通过，最终构建另做工作进程冒烟检查。

分发只需 `WordFinder.exe`，SHA-256 在 `artifacts/release/SHA256SUMS.txt`。PDB 保留在发布中间目录，不分发。组件许可证和运行时第三方声明均嵌入程序，可从“使用说明 → 开源组件与许可”查看。索引和设置始终位于外部本机用户数据目录。单文件中的原生依赖首次运行会解包到用户临时位置；相关机制见 [微软单文件文档](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)。

GitHub 更新功能只检查公开仓库最新正式 Release、比较数字版本号并展示说明及下载页；没有后台下载、自动覆盖或上传 Excel。仓库地址尚未提供，实际 WordFinder 发布下载及升级链路未验收。仓库内的 Actions 流程只构建和上传工件，不自动创建 Release。

## 尚待真实环境验收

Windows 10 具体版本和x64目标机、无预装运行时的干净系统、真实办公样本、空闲/启动 UI 内存、冷启动耗时、长时间同时办公、临时磁盘峰值与空间耗尽、实际防病毒软件及签名分发、实际仓库升级链路。当前 Windows 10 兼容目标不能代替实机测试；.NET 的官方支持还取决于系统版本/发行类型，参考 [支持列表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)。

索引体积上限暂不固定。先将代表性真实文件加入测试，记录去重率、源总量、稳定索引和更新峰值，再确定磁盘额度。当前无需用户继续确认常规实现细节，即可开始用真实样本试用这一版本。

## 复现

```powershell
dotnet build WordFinder.sln -c Release
dotnet test tests/WordFinder.Tests -c Release
./scripts/publish.ps1

$bench = 'tools/WordFinder.Benchmarks/bin/Release/net10.0-windows/WordFinder.Benchmarks.dll'
dotnet $bench generate-many artifacts/samples/many 10000
dotnet $bench generate-large artifacts/samples/large 500
dotnet $bench generate-memory artifacts/samples/memory
dotnet $bench run artifacts/samples/many artifacts/test-data/fresh-many artifacts/release/WordFinder.exe 512
dotnet $bench run artifacts/samples/large artifacts/test-data/fresh-large artifacts/release/WordFinder.exe 512
dotnet $bench run artifacts/samples/memory artifacts/test-data/fresh-memory artifacts/release/WordFinder.exe 256
dotnet $bench control artifacts/samples/large artifacts/test-data/fresh-control artifacts/release/WordFinder.exe
```

需要测首次索引时使用新的隔离数据目录；复用数据目录会变成增量测试。生成的真实500 MiB样本会占用相应磁盘，测试工件均被 .gitignore 排除。
