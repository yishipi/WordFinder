# WordFinder

面向办公室文员的本地 Excel 全文检索桌面软件。C# / .NET 10 / WinForms，ExcelDataReader 只读解析，SQLite FTS5 持久化索引。当前版本 **0.3.0**，Windows x64。

## 直接运行

从 [GitHub Releases](https://github.com/yishipi/WordFinder/releases) 下载 `WordFinder.exe`；本机构建产物位于 `artifacts/release/WordFinder.exe`。约 **50.55 MiB**，自包含压缩单 EXE，无须安装 Python、Excel 或 .NET。双击后选择本机目录，程序会自动建立和更新索引。

详细步骤见 [使用说明](docs/quick-start.md)。数据默认保存在 `%LOCALAPPDATA%\WordFinder`，替换 EXE 与索引分离。

## 已实现

- 多目录递归扫描、分类标记、重叠目录去重、分类筛选。
- `.xls`、`.xlsx`、`.xlsb`、`.xlsm` 的单元格值、隐藏内容及已保存的公式结果。
- 多目标同行搜索：目标1“张三”、目标2“上海”可以分别命中同一行的不同列。一个目标中的空格保留，英文忽略大小写，最多16个目标。
- 返回文件、路径、工作表、真实行号、命中坐标和内容预览；双击文件名打开文件、双击路径打开文件夹、双击行号在已安装的 Excel 中只读定位；支持复制内容/整行/路径、分页。
- 打开软件时自动核对并监听文件变化，支持手动增量更新；未变化文件跳过，删除同步；文件更新原子切换，失败保留旧成功版本，支持重试和中断恢复。
- 独立低优先级索引进程，Windows Job Object 限制提交内存，默认512 MiB；暂停、继续、停止；内存不足记录失败并继续其他文件。
- 文本去重、长文本压缩、精简倒排索引、空间统计、整理压缩及清空重建。
- GitHub Releases 手动检查新版并打开下载页；仓库在设置中配置。

## 构建和测试

开发环境需要 Windows 和 .NET SDK 10；`global.json` 固定10.0.401并允许同代较新特性版本。最终用户无需安装 SDK。

```powershell
dotnet build WordFinder.sln -c Release
dotnet test tests/WordFinder.Tests -c Release
./scripts/publish.ps1
```

发布脚本输出 `artifacts/release/WordFinder.exe` 和 `SHA256SUMS.txt`。调试符号保存在发布中间目录，不需要分发。GitHub Actions 在推送 v 开头的版本标签时构建、测试并创建 Release。

## 验证和边界

16项集成测试通过，覆盖真实行列、同行组合、合并单元格、文件监听、中文和符号、隐藏内容、公式缓存、源文件只读、增量删除、失败重试、分类重叠、分页及取消恢复。此前版本已测试一万个合成小文件、500 MiB 合成工作表和内存受限恢复，详见 [实现与验证报告](docs/implementation-and-validation.md)。

当前验证主机为 Windows 11 x64，**Windows 10 实机、无预装运行时的干净系统、实际办公文件集和杀毒软件验收仍待完成**。ExcelDataReader 可能缓存共享字符串表，大文件在低额度下可能失败；可提高额度重试。合成数据的耗时和索引比例不代表真实数据承诺。

当前不支持密码加密文件、公式重算、OCR、批注检索、关闭软件后的自动目录监控、自动安装升级或索引加密。变更判断基于文件大小和修改时间。EXE 未签名。

## 文档和工程

- [需求与技术选型，包括 ExcelDataReader 与 calamine 比较](docs/requirements-and-architecture.md)
- [使用说明](docs/quick-start.md)
- [实现与验证](docs/implementation-and-validation.md)
- [第三方组件许可](THIRD-PARTY-NOTICES.md)，完整文本也内嵌在程序使用说明中。
- `src/WordFinder.Core`：读取、索引、查询、数据管理。
- `src/WordFinder.Desktop`：中文 UI、独立工作进程、资源限制、更新检查。
- `tests/WordFinder.Tests`：真实 SQLite 和生成工作簿的集成测试。
- `tools/WordFinder.Benchmarks`：可重现的样本生成、工作进程基准与查询工具。
