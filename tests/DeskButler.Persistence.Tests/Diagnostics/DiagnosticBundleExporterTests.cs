using System.IO.Compression;
using System.Text.Json;
using DeskButler.Persistence.Diagnostics;

namespace DeskButler.Persistence.Tests.Diagnostics;

public sealed class DiagnosticBundleExporterTests
{
    /// <summary>预览和 ZIP 都只保留白名单诊断文件，并递归删除敏感字段、脱敏路径与标题。</summary>
    [Fact]
    public async Task PreviewThenExportRedactsNestedSecretsAndProfilePath()
    {
        using var fixture = new TempDirectory();
        var log = Path.Combine(fixture.Path, "deskbutler.jsonl");
        await File.WriteAllTextAsync(log,
            "{\"path\":\"C:\\\\Users\\\\Alice\\\\Secret\\\\plan.docx\",\"title\":\"秘密计划\",\"nested\":{\"ToKeN\":\"abc\",\"password\":\"p\",\"clipboard\":\"c\",\"commandLine\":\"bad\"}}\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(fixture.Path, "not-approved.txt"), "private document", TestContext.Current.CancellationToken);
        var exporter = new DiagnosticBundleExporter(
            fixture.Path, @"C:\Users\Alice", ["deskbutler.jsonl"]);

        var manifest = await exporter.CreateManifestAsync(TestContext.Current.CancellationToken);
        Assert.Single(manifest.Files);
        Assert.Equal("deskbutler.jsonl", manifest.Files[0].ArchiveName);
        using var previewJson = JsonDocument.Parse(manifest.Files[0].Preview);
        Assert.Equal(@"%USERPROFILE%\Secret\plan.docx", previewJson.RootElement.GetProperty("path").GetString());
        Assert.Equal("[已脱敏]", previewJson.RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("abc", manifest.Files[0].Preview, StringComparison.Ordinal);
        Assert.DoesNotContain("commandLine", manifest.Files[0].Preview, StringComparison.OrdinalIgnoreCase);

        var zipPath = Path.Combine(fixture.Path, "bundle.zip");
        await exporter.ExportAsync(manifest, zipPath, TestContext.Current.CancellationToken);
        using var archive = ZipFile.OpenRead(zipPath);
        Assert.Equal(["deskbutler.jsonl", "manifest.json"], archive.Entries.Select(item => item.FullName).Order().ToArray());
        using var reader = new StreamReader(archive.GetEntry("deskbutler.jsonl")!.Open());
        var exported = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("秘密计划", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("password", exported, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>白名单中的越界路径和重解析点不得进入预览或 ZIP。</summary>
    [Fact]
    public async Task TraversalAndReparsePointsAreRejected()
    {
        using var fixture = new TempDirectory();
        var exporter = new DiagnosticBundleExporter(fixture.Path, fixture.Path, ["..\\outside.jsonl"]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => exporter.CreateManifestAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>并发写入留下的最后半条 JSON 不得让已完整诊断记录无法预览。</summary>
    [Fact]
    public async Task IncompleteTrailingRecordIsIgnoredDuringConcurrentPreview()
    {
        using var fixture = new TempDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Path, "deskbutler.jsonl"),
            "{\"message\":\"complete\"}\n{\"message\":\"partial",
            TestContext.Current.CancellationToken);
        var exporter = new DiagnosticBundleExporter(fixture.Path, fixture.Path, ["deskbutler.jsonl"]);

        var manifest = await exporter.CreateManifestAsync(TestContext.Current.CancellationToken);

        Assert.Contains("complete", Assert.Single(manifest.Files).Preview, StringComparison.Ordinal);
        Assert.DoesNotContain("partial", manifest.Files[0].Preview, StringComparison.Ordinal);
    }

    /// <summary>保留清单名、大小写和斜杠规范化后的冲突必须在预览阶段拒绝。</summary>
    [Theory]
    [InlineData("manifest.json", "deskbutler.jsonl")]
    [InlineData("MANIFEST.JSON", "deskbutler.jsonl")]
    [InlineData("a\\b.jsonl", "a/b.jsonl")]
    [InlineData("a//b.jsonl", "deskbutler.jsonl")]
    [InlineData("a/./b.jsonl", "deskbutler.jsonl")]
    public async Task ConflictingOrNonCanonicalArchiveNamesAreRejected(string first, string second)
    {
        using var fixture = new TempDirectory();
        var exporter = new DiagnosticBundleExporter(fixture.Path, fixture.Path, [first, second]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => exporter.CreateManifestAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>父目录 junction 指向外部敏感文件时必须按句柄最终路径拒绝且不产生 ZIP 临时文件。</summary>
    [Fact]
    public async Task ParentDirectoryJunctionOutsideRootIsRejectedWithoutZipArtifacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(outside.Path, "secret.jsonl"), "{\"token\":\"outside-secret\"}\n",
            TestContext.Current.CancellationToken);
        var link = Path.Combine(root.Path, "linked");
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                   "cmd.exe", $"/c mklink /J \"{link}\" \"{outside.Path}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        })!)
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
        }

        var exporter = new DiagnosticBundleExporter(root.Path, root.Path, ["linked/secret.jsonl"]);
        var zip = Path.Combine(root.Path, "bundle.zip");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => exporter.CreateManifestAsync(TestContext.Current.CancellationToken));

        Assert.False(File.Exists(zip));
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp-*"));
        Directory.Delete(link);
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DeskButler.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        /// <summary>删除本测试创建的隔离目录。</summary>
        public void Dispose() => Directory.Delete(Path, true);
    }
}
