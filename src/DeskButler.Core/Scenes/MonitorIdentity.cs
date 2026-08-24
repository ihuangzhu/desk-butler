namespace DeskButler.Core.Scenes;

/// <summary>标识窗口所在显示器及其可用工作区。</summary>
/// <param name="DeviceName">显示器设备名称。</param>
/// <param name="WorkArea">显示器可用于放置窗口的工作区。</param>
/// <param name="DpiX">显示器水平 DPI。</param>
/// <param name="DpiY">显示器垂直 DPI。</param>
public sealed record MonitorIdentity(string DeviceName, WindowBounds WorkArea, uint DpiX, uint DpiY);
