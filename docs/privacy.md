# 隐私与本地数据说明

DeskButler 0.1.0 的长期工作现场、设置和诊断保存在当前 Windows 用户的 `%LOCALAPPDATA%\DeskButler`。检查数据库模式时还会在 `%TEMP%\DeskButler.SchemaInspection\<随机 GUID>` 短暂建立完整数据库副本，具体生命周期见下文。核心功能不需要账号、网络或云端服务，应用不会自动上传诊断包。

## 实际存储字段

`deskbutler.db` 的快照记录包含：

- 快照 `id`、格式版本、捕获时间、捕获原因、有效/无效状态和无效原因；
- 每个项目的稳定项目 id；
- 可执行文件完整路径；
- Windows 窗口类名；
- 用于匹配的窗口标题提示；
- 资源管理器窗口的完整目录路径；
- 窗口 left、top、width、height；
- 窗口状态（普通、最小化、最大化）；
- 显示器设备名、工作区矩形、DPI X/Y；
- 捕获时窗口是否为管理员权限或不可访问；
- 数据库模式还预留恢复运行 id、场景 id、开始/完成时间和结果 JSON，但 0.1.0 当前代码没有向该表写入记录。

同一数据库的 `failure_history` 只包含场景项目 id 和连续失败次数（1–3），不是逐次恢复结果明细。成功恢复会清除此项目计数；失败会增加计数；跳过和取消不改变计数。

`settings.json` 包含：是否捕获、是否登录启动、恢复卡隐藏秒数、永久排除的可执行文件完整路径列表。损坏设置会改名保留为 `settings.corrupt-<时间>-<随机>.json`，然后使用默认设置。

结构化日志每条包含时间、级别、类别、消息和经过校验的属性。0.1.0 实际写入的是异常健康事件：桌面变化检测失败及异常类型、数据库恢复健康警告及故障备份目录名，以及“恢复已完成但失败历史未能持久化”及异常类型；它不会记录每个恢复项目的成功、失败、取消状态或恢复错误明细。标题和路径会存入本机数据库；日志消息或属性也可能间接透露备份名、程序名或目录结构，因此这些内容都应按敏感信息处理。标题可能暴露文档名，路径可能暴露用户名、客户名或项目结构。

`run.lock` 只含随机运行 token、进程 id 和 UTC 启动时间，用于识别上次非正常退出；它不是账号 token。正常退出时按持有的文件身份删除。`database-recovery.marker.json` 保存格式版本、恢复 id、备份完整路径、恢复阶段，以及各证据的文件名、长度、SHA-256。恢复成功后删除；恢复中断或失败时保留，以便下次安全继续。

以下文件是原子写入或互斥所需的短期文件：

- `settings.json.tmp` 是保存设置时生成的完整新设置；替换成功或正常返回/报错时由 `finally` 删除，但若进程或电脑在写入中突然中止，可能残留。
- `database-recovery.marker.json.tmp` 是写入恢复阶段 marker 时生成的完整新 marker；移动成功或正常返回/报错时由 `finally` 删除，突然中止时可能残留。
- `diagnostics\logs\deskbutler.writer.lock` 是日志单写者锁；程序不会向它写入内容，新建时为空。日志对象存活期间以独占、关闭即删除方式持有；正常退出乃至进程终止后句柄关闭时应消失，只有文件系统或清理异常时才可能看见残留。

## 精确位置

- `%LOCALAPPDATA%\DeskButler\deskbutler.db`
- `%LOCALAPPDATA%\DeskButler\deskbutler.db-wal`、`deskbutler.db-shm`（SQLite 运行时可能出现）
- `%LOCALAPPDATA%\DeskButler\settings.json`
- `%LOCALAPPDATA%\DeskButler\settings.json.tmp`（仅设置保存期间；突然中止时可能残留）
- `%LOCALAPPDATA%\DeskButler\run.lock`
- `%LOCALAPPDATA%\DeskButler\database-recovery.marker.json`（仅恢复中断时）
- `%LOCALAPPDATA%\DeskButler\database-recovery.marker.json.tmp`（仅 marker 原子写入期间；突然中止时可能残留）
- `%LOCALAPPDATA%\DeskButler\settings.corrupt-*.json`（仅设置损坏时）
- `%LOCALAPPDATA%\DeskButler\diagnostics\logs\deskbutler.jsonl`
- `%LOCALAPPDATA%\DeskButler\diagnostics\logs\deskbutler.1.jsonl`
- `%LOCALAPPDATA%\DeskButler\diagnostics\logs\deskbutler.2.jsonl`
- `%LOCALAPPDATA%\DeskButler\diagnostics\logs\deskbutler.writer.lock`（仅日志写者运行期间；通常关闭即删除）
- `%LOCALAPPDATA%\DeskButler\diagnostics\database.corrupt-<UTC时间>-<随机>\`（数据库故障证据）
- `%TEMP%\DeskButler.SchemaInspection\<随机 GUID>\deskbutler.db`，以及源文件存在时的 `deskbutler.db-wal`、`deskbutler.db-shm`（数据库模式检查临时副本）

每次打开已有数据库时，模式检查会把当时存在的主库、WAL、SHM **完整复制**到新的 `%TEMP%\DeskButler.SchemaInspection\<随机 GUID>`，只读检查完成或正常报错时在 `finally` 中递归删除该 GUID 目录。若应用崩溃、进程被强制结束或电脑突然断电，目录可能来不及删除并保留数据库中的全部敏感字段。确认 DeskButler 已完全退出后，可以在资源管理器中检查 `%TEMP%\DeskButler.SchemaInspection`，只删除其中确认属于已结束检查的旧 GUID 子目录；这些临时副本不用于数据库故障恢复。

程序文件默认位于 `%LOCALAPPDATA%\Programs\DeskButler`，开始菜单快捷方式位于当前用户开始菜单目录。登录启动只维护 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下名为 `DeskButler` 的一个值。

## 明确不存储

不存储屏幕截图或录像、键盘输入、鼠标轨迹、剪贴板、文档正文、未保存内容、完整命令行、账号 token、密码或远程控制凭据。首版也没有云同步、遥测账号或远程命令通道。

## 诊断白名单、脱敏和预览

当前桌面宿主只把日志目录中的 `deskbutler.jsonl`、`deskbutler.1.jsonl`、`deskbutler.2.jsonl` 列入诊断白名单。导出器拒绝绝对路径、越界路径、ZIP 源文件、重解析点和不安全归档名；ZIP 另含只列文件名与字节数的 `manifest.json`。

在预览/导出快照中：递归删除名为 `commandLine`、`token`、`password`、`clipboard` 的字段；标题字段整值替换为 `[已脱敏]`；其他字符串中的用户目录前缀替换为 `%USERPROFILE%`。这不是对任意消息的语义审查，消息或非标题字段仍可能透露程序名和目录结构，所以必须先看“诊断”页预览，再分享。

当前 UI 只提供预览，不提供 ZIP 导出按钮，也不会上传。若未来接通导出，必须导出预览过的同一快照，不得绕过白名单。

## 数据库损坏时的证据

检测到 SQLite 损坏码或迁移失败后，DeskButler 先关闭连接，把存在的主库、WAL、SHM 复制到唯一 `database.corrupt-*` 目录，计算长度和 SHA-256 并持有已验证证据，再删除工作副本并创建新库。原始故障副本不会作为恢复流程的一部分删除；若重建失败，备份和 marker 都保留，并在诊断页显示健康警告。
