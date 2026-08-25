using System.Xml.Linq;
using DeskButler.Desktop.Tests;

namespace DeskButler.Desktop.Tests.Views;

/// <summary>验证恢复卡声明式视图保持保护提示、折叠和无障碍契约。</summary>
public sealed class RecoveryCardWindowTests
{
    private static readonly string RepositoryRoot = TestRepository.Root;
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>连续失败保护原因必须在可点击项内呈现、空值折叠并供辅助技术读取。</summary>
    [Fact]
    public void ProtectionReasonIsVisibleCollapsedWhenNullAndExposedToAutomation()
    {
        var document = XDocument.Load(Path.Combine(
            RepositoryRoot, "src", "DeskButler.Desktop", "Views", "RecoveryCardWindow.xaml"));
        var checkBox = document.Descendants(Presentation + "CheckBox")
            .Single(element => string.Equals(
                (string?)element.Attribute("IsChecked"), "{Binding IsSelected}", StringComparison.Ordinal));

        Assert.Equal("{Binding DisplayName}", (string?)checkBox.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding ProtectionReason}",
            (string?)checkBox.Attribute("AutomationProperties.HelpText"));
        var content = Assert.Single(checkBox.Elements(Presentation + "StackPanel"));
        Assert.Contains(content.Elements(Presentation + "TextBlock"),
            element => string.Equals(
                (string?)element.Attribute("Text"), "{Binding DisplayName}", StringComparison.Ordinal));
        var protectionReason = Assert.Single(content.Elements(Presentation + "TextBlock"),
            element => string.Equals(
                (string?)element.Attribute("Text"), "{Binding ProtectionReason}", StringComparison.Ordinal));
        var trigger = Assert.Single(protectionReason
            .Descendants(Presentation + "DataTrigger"));
        Assert.Equal("{Binding ProtectionReason}", (string?)trigger.Attribute("Binding"));
        Assert.Equal("{x:Null}", (string?)trigger.Attribute("Value"));
        var collapsed = Assert.Single(trigger.Elements(Presentation + "Setter"));
        Assert.Equal("Visibility", (string?)collapsed.Attribute("Property"));
        Assert.Equal("Collapsed", (string?)collapsed.Attribute("Value"));
    }
}
