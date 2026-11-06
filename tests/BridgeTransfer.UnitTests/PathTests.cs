using BridgeTransfer.Core.Paths;

namespace BridgeTransfer.UnitTests;

public class PathTests
{
    [Theory]
    [InlineData("/", "a", "/a")]
    [InlineData("/data", "Project X", "/data/Project X")]
    [InlineData("/data/", "b", "/data/b")]
    public void Remote_combine_uses_forward_slash_and_keeps_case(string dir, string name, string expected) =>
        Assert.Equal(expected, RemotePath.Combine(dir, name));

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("x\0y")]
    public void Remote_combine_rejects_non_child_names(string name) =>
        Assert.Throws<ArgumentException>(() => RemotePath.Combine("/data", name));

    [Fact]
    public void Backslash_is_an_ordinary_character_in_remote_names()
    {
        Assert.Equal("/data/a\\b", RemotePath.Combine("/data", "a\\b"));
        Assert.Equal("a\\b", RemotePath.GetName("/data/a\\b"));
        Assert.NotNull(WindowsNameRules.Validate("a\\b"));
    }

    [Theory]
    [InlineData("/a/b/c", "/a/b")]
    [InlineData("/a", "/")]
    [InlineData("/a/b/", "/a")]
    public void Remote_parent(string path, string parent) => Assert.Equal(parent, RemotePath.GetParent(path));

    [Fact]
    public void Remote_root_has_no_parent() => Assert.Null(RemotePath.GetParent("/"));

    [Theory]
    [InlineData("/home/u//Data/./x/../y", "/home/u", "/home/u/Data/y")]
    [InlineData("~/Data", "/home/u", "/home/u/Data")]
    [InlineData("rel/Path", "/home/u", "/home/u/rel/Path")]
    [InlineData("/../..", "/home/u", "/")]
    [InlineData("  /CaseMatters ", "/home/u", "/CaseMatters")]
    public void Remote_user_input_normalization(string input, string home, string expected) =>
        Assert.Equal(expected, RemotePath.NormalizeUserInput(input, home));

    [Theory]
    [InlineData("/data", "/data/x", true)]
    [InlineData("/data", "/data", true)]
    [InlineData("/data", "/database", false)]
    [InlineData("/data", "/Data/x", false)]
    [InlineData("/", "/anything", true)]
    public void Remote_containment_is_case_sensitive_and_segment_aware(string root, string path, bool within) =>
        Assert.Equal(within, RemotePath.IsWithin(root, path));

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../b")]
    [InlineData("/abs")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    public void Relative_paths_reject_traversal(string rel) =>
        Assert.Throws<ArgumentException>(() => RelativePath.Split(rel));

    [Fact]
    public void Local_combine_refuses_escape_and_stream_syntax()
    {
        var root = Path.Combine(Path.GetTempPath(), "bt-root");
        Assert.Throws<ArgumentException>(() => LocalPath.Combine(root, ".."));
        Assert.Throws<ArgumentException>(() => LocalPath.Combine(root, "a\\..\\..\\x"));
        Assert.Throws<ArgumentException>(() => LocalPath.Combine(root, "file.txt:stream"));
        Assert.True(LocalPath.IsWithin(root, LocalPath.CombineRelative(root, "a/b/c.txt")));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("Aux.tar.gz")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("lpt9.log")]
    [InlineData("COM¹")]
    [InlineData("a<b")]
    [InlineData("q?")]
    [InlineData("star*")]
    [InlineData("pipe|x")]
    [InlineData("quote\"")]
    [InlineData("data.csv:Zone.Identifier")]
    [InlineData("trailing ")]
    [InlineData("trailing.")]
    [InlineData("tab\tname")]
    public void Windows_rejects_unrepresentable_names(string name) => Assert.NotNull(WindowsNameRules.Validate(name));

    [Theory]
    [InlineData("CONSOLE.txt")]
    [InlineData("results 2024.csv")]
    [InlineData(".bashrc")]
    [InlineData("naïve-ü-日本.txt")]
    [InlineData("COM10")]
    public void Windows_accepts_valid_names(string name) => Assert.Null(WindowsNameRules.Validate(name));

    [Fact]
    public void Case_collisions_are_grouped()
    {
        var groups = WindowsNameRules.FindCaseCollisions(new[] { "Readme", "README", "readme", "other", "Other.txt" });
        Assert.Single(groups);
        Assert.Equal(3, groups[0].Count);
    }

    [Fact]
    public void Breadcrumbs_cover_each_segment()
    {
        var crumbs = RemotePath.Breadcrumbs("/home/user/My Data");
        Assert.Equal(new[] { "/", "/home", "/home/user", "/home/user/My Data" }, crumbs.Select(c => c.Path));
    }
}
