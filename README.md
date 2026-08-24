# DeskButler

DeskButler 是一个面向 Windows 的本地桌面工作空间助手。

## 解决方案结构

- `src/`：应用程序与各层实现。
- `tests/`：与源项目对应的自动化测试。
- `docs/`：已批准的设计与实施计划副本。

## 先决条件

- .NET SDK 10.0.400 或更高版本
- Windows x64

## 本地验证

```text
dotnet restore DeskButler.slnx
dotnet build DeskButler.slnx -c Debug --no-restore
dotnet test DeskButler.slnx -c Debug --no-build
```
