using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DeskButler.Desktop.Tests;

public sealed class InstallerContractTests
{
    private static readonly string RepositoryRoot = TestRepository.Root;

    /// <summary>验证 Release 发布是完整的 win-x64 自包含非单文件布局。</summary>
    [Fact]
    public void Release发布配置生成非单文件自包含WinX64产物()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot, "src", "DeskButler.Desktop", "DeskButler.Desktop.csproj"));
        var release = project.Root!.Elements("PropertyGroup")
            .Single(group => string.Equals((string?)group.Attribute("Condition"),
                "'$(Configuration)' == 'Release'", StringComparison.Ordinal));

        Assert.Equal("win-x64", release.Element("RuntimeIdentifier")?.Value);
        Assert.Equal("true", release.Element("SelfContained")?.Value);
        Assert.Equal("false", release.Element("PublishSingleFile")?.Value);
        Assert.Equal("true", release.Element("PublishReadyToRun")?.Value);
        Assert.Equal("embedded", release.Element("DebugType")?.Value);
    }

    /// <summary>验证 Release 符号嵌入程序集并映射源码根，避免泄露开发机绝对路径。</summary>
    [Fact]
    public void Release构建统一嵌入符号并映射本机源码路径()
    {
        var props = XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Build.props"));
        var release = props.Root!.Elements("PropertyGroup")
            .Single(group => string.Equals((string?)group.Attribute("Condition"),
                "'$(Configuration)' == 'Release'", StringComparison.Ordinal));

        Assert.Equal("embedded", release.Element("DebugType")?.Value);
        Assert.Equal("true", release.Element("DebugSymbols")?.Value);
        Assert.Equal("$(MSBuildProjectDirectory)=/_/$(MSBuildProjectName)", release.Element("PathMap")?.Value);
    }

    /// <summary>验证安装声明限制为当前用户固定目录和稳定应用身份。</summary>
    [Fact]
    public void 安装脚本限定当前用户固定目录和稳定应用身份()
    {
        var script = ReadInstallerScript();

        Assert.Contains("AppId=DeskButler", script, StringComparison.Ordinal);
        Assert.Contains("DefaultDirName={localappdata}\\Programs\\DeskButler", script, StringComparison.Ordinal);
        Assert.Contains("PrivilegesRequired=lowest", script, StringComparison.Ordinal);
        Assert.Contains("SetupArchitecture=x64", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivilegesRequired=admin", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[Services]", script, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>验证卸载只使用固定受控命令并守卫精确用户数据目录。</summary>
    [Fact]
    public void 卸载脚本只调用固定受控命令并以精确数据路径守卫删除()
    {
        var script = ReadInstallerScript();

        Assert.Contains("--prepare-uninstall", script, StringComparison.Ordinal);
        Assert.Contains("RunOnceId: \"PrepareDeskButlerUninstall\"", script, StringComparison.Ordinal);
        Assert.Contains("IsExactUserDataPath", script, StringComparison.Ordinal);
        Assert.Contains("FILE_ATTRIBUTE_REPARSE_POINT", script, StringComparison.Ordinal);
        Assert.Contains("DelTree(UserDataPath, True, True, True)", script, StringComparison.Ordinal);
        Assert.Contains("PrepareApplicationForUninstall", script, StringComparison.Ordinal);
        Assert.Contains("Abort;", script, StringComparison.Ordinal);
        Assert.Contains("RegDeleteValue(HKCU, 'Software\\Microsoft\\Windows\\CurrentVersion\\Run', 'DeskButler')",
            script, StringComparison.Ordinal);
        Assert.DoesNotContain("taskkill", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("schtasks", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Schedule.Service", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sc.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[Services]", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("QQ.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WeChat.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Futu", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HKLM", script, StringComparison.OrdinalIgnoreCase);

        var runValueDeletes = script
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Contains("RegDeleteValue(", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Single(runValueDeletes);
        Assert.Equal(
            "RegDeleteValue(HKCU, 'Software\\Microsoft\\Windows\\CurrentVersion\\Run', 'DeskButler');",
            runValueDeletes[0]);
    }

    /// <summary>验证发布卸载夹具覆盖常驻设置与登录批次文件的保留和精确删除语义。</summary>
    [Fact]
    public void 卸载夹具覆盖整个常驻数据根的保留与删除()
    {
        var fixture = ReadUninstallFixture();

        var defaultArguments = ReadPowerShellStringArray(fixture, "defaultUninstallArguments");
        Assert.Equal(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"], defaultArguments);
        Assert.DoesNotContain("/DELETEUSERDATA=1", defaultArguments, StringComparer.OrdinalIgnoreCase);

        var deleteDataArguments = ReadPowerShellStringArray(fixture, "deleteDataUninstallArguments");
        Assert.Equal(
            ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/DELETEUSERDATA=1"],
            deleteDataArguments);
        Assert.Single(deleteDataArguments, argument =>
            string.Equals(argument, "/DELETEUSERDATA=1", StringComparison.OrdinalIgnoreCase));

        var uninstaller = NormalizePowerShellStatements(
            ReadPowerShellFunctionBody(fixture, "Uninstall-Fixture"));
        Assert.Equal(
            """
            param([string[]]$Arguments)
            $uninstaller = Join-Path $installDirectory 'unins000.exe'
            Invoke-CheckedProcess $uninstaller $Arguments
            $script:installed = $false
            """,
            uninstaller);
        Assert.DoesNotContain("$defaultUninstallArguments", uninstaller, StringComparison.Ordinal);
        Assert.DoesNotContain("$deleteDataUninstallArguments", uninstaller, StringComparison.Ordinal);

        var lifecycle = NormalizePowerShellStatements(
            ReadPowerShellFunctionBody(fixture, "Invoke-UninstallContractFixture"));
        Assert.Equal(
            """
            Verify-DefaultUninstallPreservesUserData
            Verify-DeleteDataUninstallRemovesUserData
            """,
            lifecycle);

        const string topLevelMarker = "# CANONICAL TOP-LEVEL EXECUTION";
        var topLevelText = ReadPowerShellTopLevelText(fixture);
        Assert.DoesNotMatch(@"(?im)^[ \t]*function\b", topLevelText);
        var markerIndex = topLevelText.LastIndexOf(topLevelMarker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"PowerShell top-level marker was not found: {topLevelMarker}");
        var terminatingStatements = ReadExplicitPowerShellTerminations(topLevelText[..markerIndex]);
        Assert.Empty(terminatingStatements);
        Assert.Equal(
            ["return"],
            ReadExplicitPowerShellTerminations("Write-Output x; return"));
        Assert.Equal(
            ["exit 0"],
            ReadExplicitPowerShellTerminations("Write-Output x; exit 0"));
        Assert.Empty(ReadExplicitPowerShellTerminations("Write-Output x#harmless; return"));
        Assert.Empty(ReadExplicitPowerShellTerminations("Write-Output x<#plain; return"));
        Assert.Equal(["return"], ReadExplicitPowerShellTerminations("<# outer <# inner #>; return"));
        Assert.Empty(ReadExplicitPowerShellTerminations("Write-Output x#harmless; exit 0"));
        Assert.Equal(["exit 0"], ReadExplicitPowerShellTerminations("x\"y\"# comment\nexit 0"));
        Assert.Equal(["exit 0"], ReadExplicitPowerShellTerminations("x'y'<# comment #> 1; exit 0"));
        Assert.Equal(["exit 0"], ReadExplicitPowerShellTerminations("x$(1)# comment\nexit 0"));
        Assert.Equal(["exit 0"], ReadExplicitPowerShellTerminations("$x=<# comment #>1; exit 0"));
        Assert.Equal(
            ["throw 'fixture stopped'", "break", "continue"],
            ReadExplicitPowerShellTerminations(
                "Write-Output x; throw 'fixture stopped'; break; continue"));
        Assert.Equal(
            ["[Environment]::Exit(0)", "$host.SetShouldExit(0)", "Stop-Process -Id $PID"],
            ReadExplicitPowerShellTerminations(
                "Write-Output x; [Environment]::Exit(0); $host.SetShouldExit(0); Stop-Process -Id $PID"));

        const string harmlessNestedTerminations =
            """
            Write-Output "return; exit"
            Write-Output 'Stop-Process $PID; throw'
            Write-Output "escaped `"return; exit`""
            Write-Output 'escaped ''return; exit'''
            # return; exit 0
            <# $host.SetShouldExit(0); break #>
            & { Write-Output x; return }
            function Invoke-Nested { Write-Output x; exit 0 }
            """;
        Assert.Empty(ReadExplicitPowerShellTerminations(
            ReadPowerShellTopLevelText(harmlessNestedTerminations)));

        const string harmlessHereStrings =
            """
            $doubleQuotedHereString = @"
            "; return
              "@; exit 0
            "@
            $singleQuotedHereString = @'
            '; exit 0
              '@; return
            '@
            """;
        Assert.Empty(ReadExplicitPowerShellTerminations(harmlessHereStrings));

        var topLevelTail = NormalizePowerShellStatements(
            ReadPowerShellTail(fixture, topLevelMarker));
        Assert.Equal(
            """
            Initialize-UninstallFixture
            Invoke-UninstallContractFixture
            """,
            topLevelTail);

        var defaultScenario = ReadPowerShellFunctionBody(fixture, "Verify-DefaultUninstallPreservesUserData");
        Assert.Contains("Uninstall-Fixture -Arguments $defaultUninstallArguments", defaultScenario, StringComparison.Ordinal);
        Assert.Contains("Assert-DefaultUserDataPreserved", defaultScenario, StringComparison.Ordinal);
        Assert.DoesNotContain("$deleteDataUninstallArguments", defaultScenario, StringComparison.Ordinal);

        var preserveAssertions = ReadPowerShellFunctionBody(fixture, "Assert-DefaultUserDataPreserved");
        Assert.Contains("if (-not (Test-Path -LiteralPath $dataRoot))", preserveAssertions, StringComparison.Ordinal);
        Assert.Contains("if (-not (Test-Path -LiteralPath $residentSettingsMarker))", preserveAssertions, StringComparison.Ordinal);
        Assert.Contains("if (-not (Test-Path -LiteralPath $residentLaunchSessionMarker))", preserveAssertions, StringComparison.Ordinal);

        var deleteScenario = ReadPowerShellFunctionBody(fixture, "Verify-DeleteDataUninstallRemovesUserData");
        Assert.Contains("Uninstall-Fixture -Arguments $deleteDataUninstallArguments", deleteScenario, StringComparison.Ordinal);
        Assert.Contains("Assert-DeletedUserDataRoot", deleteScenario, StringComparison.Ordinal);
        Assert.DoesNotContain("$defaultUninstallArguments", deleteScenario, StringComparison.Ordinal);

        var deleteAssertions = ReadPowerShellFunctionBody(fixture, "Assert-DeletedUserDataRoot");
        Assert.Contains("if (Test-Path -LiteralPath $dataRoot)", deleteAssertions, StringComparison.Ordinal);
        Assert.Contains("throw 'Delete-data uninstall left the DeskButler user-data root behind.'",
            deleteAssertions, StringComparison.Ordinal);
    }

    /// <summary>验证静默卸载准备失败只记录并中止，不创建任何阻塞消息框。</summary>
    [Fact]
    public void 静默卸载准备失败不会显示消息框()
    {
        var script = ReadInstallerScript();

        Assert.Contains("if not IsSilentUninstall then", script, StringComparison.Ordinal);
        Assert.Contains("SuppressibleMsgBox('DeskButler 未能安全退出", script, StringComparison.Ordinal);
        Assert.DoesNotContain("    MsgBox('DeskButler 未能安全退出", script, StringComparison.Ordinal);
    }

    /// <summary>验证发布目录中的调试符号不会被安装到用户机器。</summary>
    [Fact]
    public void 安装文件明确递归排除Pdb()
    {
        var script = ReadInstallerScript();

        Assert.Contains("Excludes: \"*.pdb\"", script, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>验证覆盖升级只清理安装目录中显式登记的旧文件。</summary>
    [Fact]
    public void 升级清理项仅允许精确程序文件路径()
    {
        var script = ReadInstallerScript();
        var entries = ReadInstallerSectionEntries(script, "InstallDelete");

        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            var name = Regex.Match(
                entry,
                "(?:^|;)\\s*Name:\\s*\"(?<value>[^\"]+)\"",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

            Assert.True(name.Success, $"升级清理项缺少固定 Name：{entry}");
            var path = name.Groups["value"].Value;
            Assert.StartsWith("{app}\\", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('*', path);
            Assert.DoesNotContain('?', path);
            Assert.DoesNotContain("..", path, StringComparison.Ordinal);
            Assert.DoesNotContain("{localappdata}", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unins", path, StringComparison.OrdinalIgnoreCase);
        });

        Assert.Contains(entries, entry =>
            entry.Contains("Name: \"{app}\\installed-version.txt\"", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>验证卸载初始化重入时复用首次数据保留决定。</summary>
    [Fact]
    public void 卸载数据选择只决定一次()
    {
        var script = ReadInstallerScript();

        Assert.Contains("DeleteUserDataDecisionMade: Boolean", script, StringComparison.Ordinal);
        Assert.Contains("if DeleteUserDataDecisionMade then", script, StringComparison.Ordinal);
        Assert.Contains("DeleteUserDataDecisionMade := True", script, StringComparison.Ordinal);
    }

    /// <summary>验证安装后的独立版本标记可用于证明覆盖升级确实替换了内容。</summary>
    [Fact]
    public void 安装完成写入并卸载版本标记()
    {
        var script = ReadInstallerScript();

        Assert.Contains("installed-version.txt", script, StringComparison.Ordinal);
        Assert.Contains("SaveStringToFile", script, StringComparison.Ordinal);
        Assert.Contains("{#AppVersion}", script, StringComparison.Ordinal);
        Assert.Contains("[UninstallDelete]", script, StringComparison.Ordinal);
    }

    /// <summary>验证安装与卸载脚本始终显式使用当前用户注册表 64 位视图。</summary>
    [Fact]
    public void 验证脚本显式使用Registry64且禁止默认视图()
    {
        foreach (var fileName in new[] { "verify-install.ps1", "verify-uninstall.ps1" })
        {
            var script = File.ReadAllText(Path.Combine(RepositoryRoot, "tests", "installer", fileName));

            Assert.Contains("[Microsoft.Win32.RegistryView]::Registry64", script, StringComparison.Ordinal);
            Assert.Contains("[Microsoft.Win32.RegistryHive]::CurrentUser", script, StringComparison.Ordinal);
            Assert.DoesNotContain("[Microsoft.Win32.Registry]::CurrentUser", script, StringComparison.Ordinal);
        }
    }

    /// <summary>读取安装脚本供声明式安全契约测试使用。</summary>
    private static string ReadInstallerScript() =>
        File.ReadAllText(Path.Combine(RepositoryRoot, "installer", "DeskButler.iss"));

    /// <summary>读取 Inno Setup 指定区段中的有效配置项。</summary>
    private static string[] ReadInstallerSectionEntries(string script, string sectionName)
    {
        var section = Regex.Match(
            script,
            $@"(?ms)^\[{Regex.Escape(sectionName)}\]\s*\r?\n(?<body>.*?)(?=^\[|\z)",
            RegexOptions.CultureInvariant);
        Assert.True(section.Success, $"安装脚本区段不存在：[{sectionName}]");

        return section.Groups["body"].Value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith(';') && !line.StartsWith("//", StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>读取发布卸载夹具供数据根行为契约测试使用。</summary>
    private static string ReadUninstallFixture() =>
        File.ReadAllText(Path.Combine(RepositoryRoot, "tests", "installer", "verify-uninstall.ps1"));

    /// <summary>读取单行 PowerShell 单引号字符串数组，稳定验证卸载参数集合。</summary>
    private static string[] ReadPowerShellStringArray(string script, string variableName)
    {
        var assignment = Regex.Match(
            script,
            $@"(?m)^\${Regex.Escape(variableName)}\s*=\s*@\((?<values>[^)]*)\)\s*$",
            RegexOptions.CultureInvariant);
        Assert.True(assignment.Success, $"PowerShell array was not found: ${variableName}");

        return Regex.Matches(
                assignment.Groups["values"].Value,
                "'(?<value>[^']*)'",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["value"].Value)
            .ToArray();
    }

    /// <summary>按配对花括号读取 PowerShell 函数体，避免断言命中其他场景的相同文本。</summary>
    private static string ReadPowerShellFunctionBody(string script, string functionName)
    {
        var declaration = $"function {functionName}";
        var declarationIndex = script.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(declarationIndex >= 0, $"PowerShell function was not found: {functionName}");
        var openingBrace = script.IndexOf('{', declarationIndex + declaration.Length);
        Assert.True(openingBrace >= 0, $"PowerShell function has no body: {functionName}");

        var closingBrace = FindMatchingPowerShellBrace(script, openingBrace, $"function {functionName}");
        return script[(openingBrace + 1)..closingBrace];
    }

    /// <summary>剔除全部函数定义及其配对函数体，仅保留真正的 PowerShell 顶层文本。</summary>
    private static string ReadPowerShellTopLevelText(string script)
    {
        var functionDeclaration = new Regex(
            @"(?m)^[ \t]*function[ \t]+[A-Za-z_][A-Za-z0-9_:-]*[ \t\r\n]*\{",
            RegexOptions.CultureInvariant);
        var topLevel = new System.Text.StringBuilder(script.Length);
        var cursor = 0;
        while (cursor < script.Length)
        {
            var match = functionDeclaration.Match(script, cursor);
            if (!match.Success)
            {
                topLevel.Append(script, cursor, script.Length - cursor);
                break;
            }

            topLevel.Append(script, cursor, match.Index - cursor);
            var openingBrace = match.Index + match.Value.LastIndexOf('{');
            cursor = FindMatchingPowerShellBrace(script, openingBrace, match.Value.Trim()) + 1;
            topLevel.AppendLine();
        }

        return topLevel.ToString();
    }

    /// <summary>按 PowerShell 顶层换行或分号切分语句，同时避开字符串、注释与嵌套结构。</summary>
    private static string[] SplitPowerShellTopLevelStatements(string source)
    {
        var statements = new List<string>();
        var statement = new System.Text.StringBuilder();
        var parenthesisDepth = 0;
        var braceDepth = 0;
        var bracketDepth = 0;
        var inBlockComment = false;
        var inSingleQuotedString = false;
        var inDoubleQuotedString = false;
        var inLineComment = false;
        var hereStringQuote = '\0';
        var atHereStringLineStart = false;
        var canStartComment = true;

        // 提交去除首尾空白的语句，忽略注释留下的纯空白片段。
        void CompleteStatement()
        {
            var normalized = statement.ToString().Trim();
            if (normalized.Length > 0)
            {
                statements.Add(normalized);
            }

            statement.Clear();
            canStartComment = true;
        }

        // 顶层换行是语句边界；嵌套结构中的换行只是普通空白。
        void CompleteLine(ref int position)
        {
            if (source[position] == '\r' &&
                position + 1 < source.Length &&
                source[position + 1] == '\n')
            {
                position++;
            }

            canStartComment = true;
            if (parenthesisDepth == 0 && braceDepth == 0 && bracketDepth == 0)
            {
                CompleteStatement();
            }
            else
            {
                statement.Append('\n');
            }
        }

        // 反引号后的字符属于当前 token；被转义的 CRLF 也不能形成语句边界。
        void AppendEscapedCharacter(ref int position)
        {
            if (position + 1 >= source.Length)
            {
                return;
            }

            statement.Append(source[++position]);
            if (source[position] == '\r' &&
                position + 1 < source.Length &&
                source[position + 1] == '\n')
            {
                statement.Append(source[++position]);
            }
        }

        // Here-string header 允许 @" / @' 后有水平空白，但必须随即换行。
        bool TryReadHereStringHeader(int position, out char quote, out int lineBreakPosition)
        {
            quote = '\0';
            lineBreakPosition = -1;
            if (source[position] != '@' || position + 1 >= source.Length ||
                source[position + 1] is not ('"' or '\''))
            {
                return false;
            }

            var cursor = position + 2;
            while (cursor < source.Length &&
                   source[cursor] is not ('\r' or '\n') &&
                   char.IsWhiteSpace(source[cursor]))
            {
                cursor++;
            }

            if (cursor >= source.Length || source[cursor] is not ('\r' or '\n'))
            {
                return false;
            }

            quote = source[position + 1];
            lineBreakPosition = cursor;
            return true;
        }

        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';

            if (hereStringQuote != '\0')
            {
                if (atHereStringLineStart && current == hereStringQuote && next == '@')
                {
                    statement.Append(current);
                    statement.Append(next);
                    index++;
                    hereStringQuote = '\0';
                    atHereStringLineStart = false;
                    canStartComment = true;
                }
                else if (current is '\r' or '\n')
                {
                    statement.Append(current);
                    if (current == '\r' && next == '\n')
                    {
                        statement.Append(next);
                        index++;
                    }

                    atHereStringLineStart = true;
                }
                else
                {
                    statement.Append(current);
                    atHereStringLineStart = false;
                }

                continue;
            }

            if (inLineComment)
            {
                if (current is '\r' or '\n')
                {
                    inLineComment = false;
                    CompleteLine(ref index);
                }

                continue;
            }

            if (inBlockComment)
            {
                if (current == '#' && next == '>')
                {
                    inBlockComment = false;
                    canStartComment = true;
                    index++;
                }

                continue;
            }

            if (inSingleQuotedString)
            {
                statement.Append(current);
                if (current == '\'' && next == '\'')
                {
                    statement.Append(next);
                    index++;
                }
                else if (current == '\'')
                {
                    inSingleQuotedString = false;
                    canStartComment = true;
                }

                continue;
            }

            if (inDoubleQuotedString)
            {
                statement.Append(current);
                if (current == '`' && next != '\0')
                {
                    AppendEscapedCharacter(ref index);
                }
                else if (current == '"' && next == '"')
                {
                    statement.Append(next);
                    index++;
                }
                else if (current == '"')
                {
                    inDoubleQuotedString = false;
                    canStartComment = true;
                }

                continue;
            }

            if (current == '`')
            {
                statement.Append(current);
                AppendEscapedCharacter(ref index);
                canStartComment = false;
                continue;
            }

            if (TryReadHereStringHeader(index, out var openingQuote, out var lineBreakPosition))
            {
                statement.Append(source, index, lineBreakPosition - index);
                index = lineBreakPosition - 1;
                hereStringQuote = openingQuote;
                atHereStringLineStart = false;
                canStartComment = false;
                continue;
            }

            if (current == '#')
            {
                inLineComment = true;
                continue;
            }

            if (current == '<' && next == '#')
            {
                inBlockComment = true;
                statement.Append(' ');
                index++;
                continue;
            }

            if (current == '\'')
            {
                inSingleQuotedString = true;
                statement.Append(current);
                continue;
            }

            if (current == '"')
            {
                inDoubleQuotedString = true;
                statement.Append(current);
                continue;
            }

            if (current is '\r' or '\n')
            {
                CompleteLine(ref index);
                continue;
            }

            if (current == ';' && parenthesisDepth == 0 && braceDepth == 0 && bracketDepth == 0)
            {
                CompleteStatement();
                continue;
            }

            statement.Append(current);
            switch (current)
            {
                case '(':
                    parenthesisDepth++;
                    break;
                case ')' when parenthesisDepth > 0:
                    parenthesisDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']' when bracketDepth > 0:
                    bracketDepth--;
                    break;
            }

            canStartComment = char.IsWhiteSpace(current) ||
                current is '(' or ')' or '{' or '}' or '[' or ']' or ';' or ',' or '|' or '&';
        }

        CompleteStatement();
        return statements.ToArray();
    }

    /// <summary>从规范化的顶层语句中筛出会终止当前 PowerShell 脚本的语句。</summary>
    private static string[] ReadExplicitPowerShellTerminations(string source) =>
        SplitPowerShellTopLevelStatements(source)
            .Where(IsExplicitPowerShellTermination)
            .ToArray();

    /// <summary>以既有花括号深度规则找到函数体结尾，供函数读取与顶层剔除共同使用。</summary>
    private static int FindMatchingPowerShellBrace(string script, int openingBrace, string context)
    {
        var depth = 0;
        for (var index = openingBrace; index < script.Length; index++)
        {
            depth += script[index] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0
            };
            if (depth == 0)
            {
                return index;
            }
        }

        throw new Xunit.Sdk.XunitException($"PowerShell body was not closed: {context}");
    }

    /// <summary>读取规范顶层执行标记后的内容，确保最终调用不藏在条件或异常分支中。</summary>
    private static string ReadPowerShellTail(string script, string marker)
    {
        var markerIndex = script.LastIndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"PowerShell top-level marker was not found: {marker}");
        return script[(markerIndex + marker.Length)..];
    }

    /// <summary>忽略空行、注释与缩进后比较规范 PowerShell 直线控制流。</summary>
    private static string NormalizePowerShellStatements(string source) =>
        string.Join(
            Environment.NewLine,
            source.Split('\n', StringSplitOptions.TrimEntries)
                .Where(line => line.Length > 0 && !line.StartsWith('#')));

    /// <summary>识别顶层会明确终止当前脚本的直线语句。</summary>
    private static bool IsExplicitPowerShellTermination(string statement) =>
        Regex.IsMatch(
            statement,
            @"^(?:(?:return|exit|throw|break|continue)(?:\s|;|$)|\[(?:System\.)?Environment\]::Exit\s*\(|\$host\.SetShouldExit\s*\(|Stop-Process\b.*(?:-Id\s+)?\$PID\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

}
