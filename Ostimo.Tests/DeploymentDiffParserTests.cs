namespace Ostimo.Tests;

using FluentAssertions;
using Ostimo.Core.Models;
using Ostimo.Core.Services;

/// <summary>
/// Tests for <see cref="DeploymentDiffParser"/> against captured
/// <c>rpm-ostree db diff</c> text output.
///
/// Per RpmOstree.md §10, this is the single highest-value test class in the
/// project: it's the most "parsing logic" heavy part of the app (a
/// hand-rolled state machine over plain text, since <c>db diff</c> has no
/// <c>--json</c> flag -- §5.2) and the most likely to silently produce wrong
/// results against unusual real-world output rather than throwing loudly.
///
/// Fixtures live in <c>SampleData/</c> as real-shaped <c>db diff</c> text
/// (not hand-abbreviated one-liners), covering every shape called out in
/// §10: empty diff, only-added, only-removed, mixed, plus one fixture with
/// the kind of whitespace/line-ending quirks real captured terminal output
/// tends to have that hand-written fixtures wouldn't reproduce.
/// </summary>
public sealed class DeploymentDiffParserTests
{
    private const string FromChecksum = "384892929bb2bd7a61ec17470c4f7adf9ffba309505a1de52927d99ccc6fcdff";
    private const string ToChecksum = "1c9d4e28b7a3f5c1d0e9b8a7c6d5e4f3a2b1c0d9384892929bb2bd7a61ec1747";

    private readonly DeploymentDiffParser _parser = new();

    [Fact]
    public void Parse_MixedDiff_ReturnsUpgradedAddedAndRemovedChanges()
    {
        var rawOutput = ReadSampleData("db-diff-sample-1-mixed.txt");

        var diff = _parser.Parse(FromChecksum, ToChecksum, rawOutput);

        diff.FromChecksum.Should().Be(FromChecksum);
        diff.ToChecksum.Should().Be(ToChecksum);
        diff.IsEmpty.Should().BeFalse();

        diff.Upgraded.Should().BeEquivalentTo(
        [
            new PackageChange(PackageChangeKind.Upgraded, "kernel", "6.12.1-200.fc40.x86_64", "6.12.3-200.fc40.x86_64"),
            new PackageChange(PackageChangeKind.Upgraded, "firefox", "133.0-1.fc40", "134.0-1.fc40"),
            new PackageChange(PackageChangeKind.Upgraded, "mesa-libGL", "24.1.7-1.fc40", "24.2.0-1.fc40"),
        ]);

        diff.Added.Should().BeEquivalentTo(
        [
            new PackageChange(PackageChangeKind.Added, "ripgrep", OldVersion: null, NewVersion: "14.1.0-2.fc40.x86_64"),
        ]);

        diff.Removed.Should().BeEquivalentTo(
        [
            new PackageChange(PackageChangeKind.Removed, "some-old-package", OldVersion: "1.0-1.fc40.x86_64", NewVersion: null),
        ]);

        diff.Changes.Should().HaveCount(5);
    }

    [Fact]
    public void Parse_EmptyDiff_ReturnsEmptyDeploymentDiff()
    {
        var rawOutput = ReadSampleData("db-diff-sample-2-empty.txt");

        var diff = _parser.Parse(FromChecksum, ToChecksum, rawOutput);

        diff.IsEmpty.Should().BeTrue();
        diff.Changes.Should().BeEmpty();
        diff.FromChecksum.Should().Be(FromChecksum);
        diff.ToChecksum.Should().Be(ToChecksum);
    }

    [Fact]
    public void Parse_NullOrWhitespaceOutput_ReturnsEmptyDeploymentDiff()
    {
        // rpm-ostree could plausibly produce a whitespace-only stdout for a
        // no-op diff -- this should behave identically to a truly empty
        // string, not throw.
        var diff = _parser.Parse(FromChecksum, ToChecksum, "   \n  \n");

        diff.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Parse_AddedOnlyDiff_ReturnsOnlyAddedChanges()
    {
        var rawOutput = ReadSampleData("db-diff-sample-3-added-only.txt");

        var diff = _parser.Parse(FromChecksum, ToChecksum, rawOutput);

        diff.Upgraded.Should().BeEmpty();
        diff.Removed.Should().BeEmpty();
        diff.Added.Should().HaveCount(3);

        diff.Added.Select(c => c.Name).Should().BeEquivalentTo(["ripgrep", "fd-find", "bat"]);
        diff.Added.Should().OnlyContain(c => c.OldVersion == null && c.NewVersion != null);
    }

    [Fact]
    public void Parse_RemovedOnlyDiff_ReturnsOnlyRemovedChanges()
    {
        var rawOutput = ReadSampleData("db-diff-sample-4-removed-only.txt");

        var diff = _parser.Parse(FromChecksum, ToChecksum, rawOutput);

        diff.Upgraded.Should().BeEmpty();
        diff.Added.Should().BeEmpty();
        diff.Removed.Should().HaveCount(2);

        diff.Removed.Select(c => c.Name).Should().BeEquivalentTo(["some-old-package", "deprecated-utility"]);
        diff.Removed.Should().OnlyContain(c => c.NewVersion == null && c.OldVersion != null);
    }

    [Fact]
    public void Parse_QuirkyWhitespaceAndCrlf_StillParsesCorrectly()
    {
        // Real captured terminal output can have CRLF line endings and
        // trailing whitespace depending on how it was captured -- the parser
        // is documented as tolerant of this, so verify it actually is rather
        // than only ever testing pristine LF-only fixtures.
        var rawOutput = ReadSampleData("db-diff-sample-5-quirky-whitespace.txt");

        var diff = _parser.Parse(FromChecksum, ToChecksum, rawOutput);

        diff.Changes.Should().HaveCount(2);

        diff.Upgraded.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new PackageChange(PackageChangeKind.Upgraded, "kernel", "6.12.1-200.fc40.x86_64", "6.12.3-200.fc40.x86_64"));

        diff.Added.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new PackageChange(PackageChangeKind.Added, "ripgrep", OldVersion: null, NewVersion: "14.1.0-2.fc40.x86_64"));
    }

    [Fact]
    public void Parse_UnrecognizedLine_IsSkippedRatherThanThrowing()
    {
        // A single malformed/unexpected line shouldn't take down the whole
        // diff view (RpmOstree.md §8.3) -- it should just be ignored, and
        // everything else in the section should still parse.
        const string rawOutput = """
            Upgraded:
              kernel 6.12.1-200.fc40.x86_64 -> 6.12.3-200.fc40.x86_64
              this line does not match the expected shape at all
              firefox 133.0-1.fc40 -> 134.0-1.fc40
            """;

        var diff = _parser.Parse(FromChecksum, ToChecksum, rawOutput);

        diff.Upgraded.Should().HaveCount(2);
        diff.Upgraded.Select(c => c.Name).Should().BeEquivalentTo(["kernel", "firefox"]);
    }

    [Fact]
    public void Parse_PopulatesFromAndToChecksumFromCallerArguments()
    {
        // The checksums are never present in the raw db diff text itself --
        // they're supplied by the caller (see IDeploymentDiffParser's doc
        // comment) since the caller is the one who invoked `db diff` with
        // these two specific commits.
        var diff = _parser.Parse("commit-a", "commit-b", rawDbDiffOutput: "");

        diff.FromChecksum.Should().Be("commit-a");
        diff.ToChecksum.Should().Be("commit-b");
    }

    private static string ReadSampleData(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SampleData", fileName));
}