# Windows 10 发布验收清单

每次验收创建独立证据记录；不得把 Windows 11 结果混入本表。安装器基准为 `DeskButler-Setup-0.1.0.exe`，预期 SHA-256 `9935FACC1B2C194E9C1609A28A7EE57A24BEB20A94448D28805FA33C10B7B29C`。

## 环境证据

- [ ] 记录日期、执行人、机器/VM 标识、Windows edition、版本及完整 OS build。
- [ ] 记录 x64、当前用户名是否为专用测试账户、DeskButler fixture/安装器版本。
- [ ] 记录每台显示器设备名、主屏、分辨率、工作区坐标（含负坐标）、缩放/DPI 与排列图。
- [ ] 对安装器重新计算 SHA-256，并与本表基准一致；记录文件大小。
- [ ] 运行 `verify-clean-account-prerequisites.ps1 -ExpectedTestAccount <专用账户>`；必须为 `READY`，不得绕过 `BLOCK`。

## 安装、现场与登录卡

- [ ] 当前用户安装不触发管理员提升；开始菜单、卸载项与 HKCU Run 精确出现。
- [ ] 打开两个 `DeskButler.TestWindow` fixture 和一个唯一临时 Explorer 目录，记录 fixture build/hash、PID、HWND、路径和初始物理像素矩形。
- [ ] 等待并确认快照生成；最近有效快照始终仅 3 份。
- [ ] 重新登录后恢复卡出现；完整等待 15 秒，确认只收起且绝不自动恢复。
- [ ] 从管家主界面手动调用恢复；缺失 fixture 返回，窗口四边相对 DPI 期望误差均不超过 8 物理像素。
- [ ] 对恢复计划执行取消；后续项目停止，已启动窗口不被关闭或回滚。
- [ ] 键盘可聚焦卡片、详情、恢复、跳过、取消；Tab 顺序可预测，Enter/Space 行为正确，焦点可见。
- [ ] 导出脱敏诊断包，记录 bundle id、包含类别和导出文件 SHA-256。

## 安装器生命周期

- [ ] 0.1.0 覆盖升级到严格更高 fixture 版本；设置、3 份快照、排除项和恢复记录保留。
- [ ] 运行中升级由受控管道自然退出，不按名称杀进程；升级后可重新启动。
- [ ] 静默卸载默认保留用户数据；程序、快捷方式、卸载项和 DeskButler Run 值清除，无关 Run 值保留。
- [ ] 重装后交互卸载只询问一次；分别验证“保留”和“删除全部数据”选择。
- [ ] 删除模式只在干净专用账户执行；确认 `%LOCALAPPDATA%\DeskButler` 清除且不影响任何其他路径。

## 资源与结论

- [ ] 设置 `DESKBUTLER_RUN_LONG_E2E=1` 和仓库外 `DESKBUTLER_EVIDENCE_DIRECTORY`，实际运行完整 30 分钟，禁止加速冒充。
- [ ] 附每分钟 CSV/JSON：private bytes、handle count、SQLite 主文件/WAL/SHM；最后 5 次句柄不得严格单调增长，有效快照恰为 3。
- [ ] 记录每项 PASS/FAIL/BLOCK、问题链接、诊断 bundle id；只有全部发布门槛 PASS 才签署 Windows 10 基线。

当前状态（2026-08-25）：已完成当前 Windows 10 机器上的自动快速项、受控短交互往返，以及独立 workload PID 的真实 30 分钟资源验证；真实重启与干净账户安装器选择链仍未执行。
