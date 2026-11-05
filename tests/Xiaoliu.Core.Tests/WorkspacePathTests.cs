using System.Text.RegularExpressions;
using Xunit;

namespace Xiaoliu.Core.Tests;

/// <summary>
/// 工作区路径卫生（ADR 0014）。
///
/// 仓库里的文档与代码注释不得写死盘符绝对路径 —— 绝对路径只允许出现在
/// <c>docs/workspace-layout.md</c> 这一张对照表里。
///
/// 这条测试是防回流的闸门：写路径时手滑写了个盘符绝对路径就该当场红掉，
/// 而不是等到某天文档对不上实际文件才被发现。
/// </summary>
public sealed class WorkspacePathTests
{
    /// <summary>需要扫描的文本类型。图片、exe 之类不看。</summary>
    private static readonly string[] ScannedExtensions =
        { ".md", ".cs", ".sh", ".bat", ".ps1", ".py", ".json" };

    /// <summary>构建产物与工具目录，不属于「入库的文档」。</summary>
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".git-worktrees", "artifacts", ".vs", "TestResults",
        // WorkBuddy 的项目记忆目录：本地工作日志，记录当时的操作位置属正常，不受本约束。
        ".workbuddy",
    };

    /// <summary>
    /// 唯一允许写绝对路径的文件 —— 它就是那张对照表。
    /// </summary>
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "docs/workspace-layout.md",
        // 本决策记录本身就是「关于绝对路径」的文档，引用违规样例是它的内容。
        "docs/adr/0014-document-path-portability.md",
    };

    /// <summary>
    /// 操作系统固定路径（hosts 文件等）。这些不是「工作区布局」，换机器也在，
    /// 所以豁免。字符串拼接构造，避免本文件自己被扫到时误报。
    /// </summary>
    private static readonly string[] AllowedPathPrefixes =
    {
        "C:" + "\\Windows" + "\\",
    };

    /// <summary>
    /// 盘符绝对路径：字母 + 冒号 + 单个斜杠。
    /// 用负向前后顾排除两类误报：URL 里的 <c>scheme://</c>（双斜杠、冒号前是字母）
    /// 与端口号里的 <c>127.0.0.1:8899</c>（冒号前是数字）。
    /// </summary>
    private static readonly Regex AbsoluteWindowsPath =
        new(@"(?<![A-Za-z0-9_])[A-Za-z]:[\\/](?![\\/])", RegexOptions.Compiled);

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Xiaoliu.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static IEnumerable<string> ScannedFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] subdirs;
            string[] files;

            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                if (!ExcludedDirectories.Contains(Path.GetFileName(sub)))
                {
                    pending.Push(sub);
                }
            }

            foreach (var file in files)
            {
                if (ScannedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    [Fact]
    public void 仓库里除对照表外没有盘符绝对路径()
    {
        var root = RepoRoot();
        Assert.NotNull(root);

        var offenders = new List<string>();

        foreach (var file in ScannedFiles(root!))
        {
            var relative = Relative(root!, file);
            if (AllowedFiles.Contains(relative))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var match = AbsoluteWindowsPath.Match(lines[i]);
                if (!match.Success)
                {
                    continue;
                }

                // 豁免按「整行是否以已知系统路径开头」判断：
                // 正则只匹配到盘符加冒号加斜杠这一小段，光比前缀会漏掉后面的路径。
                var trimmed = lines[i].TrimStart(' ', '\t', '-', '*', '`', '"', '\'');
                if (AllowedPathPrefixes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                offenders.Add($"{relative}:{i + 1}  {lines[i].Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "仓库里出现了盘符绝对路径。请改用逻辑路径（rea/、diandian-reference/ 等），" +
            "对照表见 docs/workspace-layout.md。\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void 对照表登记了被引用的逻辑路径()
    {
        var root = RepoRoot();
        Assert.NotNull(root);

        var layout = Path.Combine(root!, "docs", "workspace-layout.md");
        Assert.True(File.Exists(layout), "缺少 docs/workspace-layout.md —— 逻辑路径的对照表");

        var text = File.ReadAllText(layout);
        foreach (var logical in new[]
                 {
                     "rea/", "rea/decompiled/", "rea/decompiled-089/",
                     "diandian-reference/", "diandian-server-ops/",
                     "xiaoliu-deploy/", "xiaoliu-repo/", "diandian-client/",
                 })
        {
            Assert.Contains(logical, text);
        }
    }

    [Fact]
    public void 文档里引用外部项目的地方都指向了对照表()
    {
        var root = RepoRoot();
        Assert.NotNull(root);

        // 出现了 rea/ 这种逻辑路径、却没告诉读者对照表在哪 —— 读者照样扑空。
        var offenders = new List<string>();

        foreach (var file in ScannedFiles(root!))
        {
            var relative = Relative(root!, file);
            if (AllowedFiles.Contains(relative))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (!text.Contains("rea/", StringComparison.Ordinal))
            {
                continue;
            }

            if (!text.Contains("workspace-layout", StringComparison.Ordinal))
            {
                offenders.Add(relative);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "这些文件引用了 rea/ 逻辑路径却没有指路（缺 docs/workspace-layout.md 的提及）：\n" +
            string.Join("\n", offenders));
    }
}
