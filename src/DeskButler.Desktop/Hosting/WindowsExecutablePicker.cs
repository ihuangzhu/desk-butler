using Microsoft.Win32;
using System.IO;

namespace DeskButler.Desktop.Hosting;

/// <summary>使用 Windows 文件对话框选择绝对 .exe 路径。</summary>
public sealed class WindowsExecutablePicker : IExecutablePicker
{
    /// <summary>显示只允许选择应用程序的系统对话框；取消或无法正规化时不产生路径。</summary>
    public Task<string?> PickAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "应用程序 (*.exe)|*.exe",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return Task.FromResult<string?>(null);
        }

        try
        {
            return Task.FromResult<string?>(Path.GetFullPath(dialog.FileName));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult<string?>(null);
        }
    }
}
