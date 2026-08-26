# 兼容性与发布门槛

本页只记录已有证据。状态为 `PENDING`、`BLOCK` 或 `NEEDS_USER_CONFIRMATION` 的项目不能写成通过。

| 范围 | 状态 | 截至 2026-08-27 的证据 |
|---|---|---|
| Windows 10 x64 19045 自动快速测试 | PASS | 本轮最终 `verify-release.cmd`：644 总计、641 通过、3 个显式门禁跳过、0 失败；Release build 为 0 警告、0 错误，publish 与 Inno Setup 7.1.0 成功。 |
| Windows 10 受控短交互 | PASS | Debug/Release 的两个受控窗口、Explorer 往返和 150% DPI 定位通过，窗口四边误差门槛 8 物理像素。 |
| Windows 10 常驻应用专用 VM 链 | PENDING | 本轮未启动真实 QQ、微信或富途，也未执行缩托盘发现、顺序、去重、主动退出、立即启动例外或第三方卸载存活验证。 |
| 本构建 30 分钟资源验证 | PENDING | Release 套件按显式 `DESKBUTLER_RUN_LONG_E2E=1` 门禁跳过；旧构建证据不能替代本轮含常驻功能的 30 分钟实测。 |
| Windows 11 x64 | PENDING | 当前机器不是 Windows 11，尚未执行真实 Windows 11 清单。 |
| 干净账户安装/升级/保留与删除卸载 | BLOCK | 当前账户不是获准的专用干净 VM；未执行真实安装或卸载。自动契约覆盖默认保留整个数据根与 `/DELETEUSERDATA=1` 精确删除，但不等同于实体链。 |
| 真实重启恢复 | NEEDS_USER_CONFIRMATION | 已停在重启前清单；没有调用重启、注销或关机命令。 |
| 诊断 ZIP 用户导出与 bundle id | PENDING | 脱敏预览已实现；当前 UI 没有 ZIP 导出按钮，人工清单尚无 bundle id。 |
| 产品代码签名 | PENDING | Inno 编译器签名有效；DeskButler 安装器本身未签名。 |

## 支持边界

- 目标：Windows 10/11 x64，最低目标框架基线 Windows 10.0.17763；首版不支持 ARM64、macOS、Linux。
- 当前有实机证据的系统只有 Windows 10 22H2 build 19045。Windows 11 必须在真实系统或独立 VM 重跑，不能复用 Win10 结果。
- 普通用户权限运行，不安装服务或驱动，不请求管理员提升。
- 管理员权限窗口或由于权限无法读取的窗口会标为不安全并跳过；应用不会绕过 Windows 权限边界。
- 保存显示器不存在时，位置器优先使用主显示器（否则第一台可用显示器），按 DPI 调整并把窗口约束到工作区，保证可见标题栏和在空间足够时至少 200×120。
- 同一程序有多个相似窗口而无法建立严格一对一匹配时会跳过，宁可少恢复也不重复乱开。
- Explorer 仅恢复当时保存且现在仍存在的本地绝对目录。浏览器标签页、IDE 内部项目、终端会话和未保存内容不在支持范围。
- 不关闭当前多余程序，不结束进程，不回滚已完成恢复动作。

## 多实例和用户会话

每个用户会话使用稳定命名 mutex 保证 DeskButler 单实例；维护命名管道按当前用户 SID 摘要和 SessionId 隔离。第二实例不会提供任意命令入口。快速用户切换会形成不同会话，各自只操作当前用户的 HKCU 和 LocalAppData；跨会话协调、会话合并及共享快照未列入首版保证。

## 发布判定

恢复卡快照、常驻应用发现/设置/登录批次、设置并发补偿和模块生命周期观察的内部状态一致性缺口已纳入自动测试；这不会替代外部环境证据。当前只能称“本地 release candidate / 自动化候选”。正式发布至少还需要：Windows 10 专用 VM 的 QQ/微信/富途完整清单 PASS、Windows 11 独立清单 PASS、干净专用账户安装与删除链 PASS、用户当下确认后的真实重启 PASS、本构建 30 分钟资源测试 PASS、诊断 bundle 导出链闭环、产品代码签名，并由独立审查确认结果。任何 `BLOCK/PENDING/NEEDS_USER_CONFIRMATION` 都不能折算成 PASS。
