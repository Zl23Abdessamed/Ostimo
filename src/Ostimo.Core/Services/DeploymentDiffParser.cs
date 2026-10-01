using System.Text.RegularExpressions;

namespace Ostimo.Core.Services;

using Ostimo.Core.Models;

// Note: uses System.Linq (Select, ToList) and collection expressions. Assumes
// <ImplicitUsings>enable</ImplicitUsings> in the .csproj; add an explicit
// `using System.Linq;` if that's not set.

/// <summary>
/// Parses <c>rpm-ostree db diff</c> plain-text output.
///
/// Expected shape (confirmed in RpmOstree.md §5.2), any subset of these three
/// section headers, in any order, each followed by two-space-indented lines:
///
/// <code>
/// Upgraded:
///   kernel 6.12.1-200.fc40.x86_64 -&gt; 6.12.3-200.fc40.x86_64
/// Added:
///   ripgrep 14.1.0-2.fc40.x86_64
/// Removed:
///   some-old-package 1.0-1.fc40.x86_64
/// </code>
///
/// This is a small state machine: track which section we're currently inside
/// as we read lines top to bottom, and parse each data line according to
/// that section's shape. There's no JSON to fall back on for this command
/// (RpmOstree.md §5.2), so the parser has to be tolerant of minor formatting
/// variance (blank lines, trailing whitespace, CRLF line endings from odd
/// terminal captures) rather than assuming a pristine fixture every time.
/// </summary>
public sealed class DeploymentDiffParser : IDeploymentDiffParser
{
    private const string UpgradedHeader = "Upgraded:";
    private const string AddedHeader = "Added:";
    private const string RemovedHeader = "Removed:";

    // "<name> <oldVersion> -> <newVersion>", e.g.
    // "kernel 6.12.1-200.fc40.x86_64 -> 6.12.3-200.fc40.x86_64"
    // Name is everything up to the first run of whitespace; old/new versions
    // are whatever sits on either side of the arrow. Tolerant of the arrow
    // being "->" with one-or-more spaces around it, since that's the only
    // part of the shape that's remotely likely to vary by rpm-ostree version.
    private static readonly Regex UpgradedLinePattern = new(
        @"^(?<name>\S+)\s+(?<old>\S+)\s*->\s*(?<new>\S+)\s*$",
        RegexOptions.Compiled);

    // "<name> <version>", e.g. "ripgrep 14.1.0-2.fc40.x86_64" -- used for
    // both Added and Removed lines, which share this shape.
    private static readonly Regex AddedOrRemovedLinePattern = new(
        @"^(?<name>\S+)\s+(?<version>\S+)\s*$",
        RegexOptions.Compiled);

    private enum Section
    {
        None,
        Upgraded,
        Added,
        Removed,
    }

    public DeploymentDiff Parse(string fromChecksum, string toChecksum, string rawDbDiffOutput)
    {
        ArgumentNullException.ThrowIfNull(fromChecksum);
        ArgumentNullException.ThrowIfNull(toChecksum);

        var changes = new List<PackageChange>();

        if (string.IsNullOrWhiteSpace(rawDbDiffOutput))
        {
            return new DeploymentDiff(fromChecksum, toChecksum, changes);
        }

        var currentSection = Section.None;

        // Normalize line endings so a CRLF capture (e.g. from a terminal on
        // an unusual setup) doesn't leave a trailing '\r' stuck to the last
        // token of each line.
        var lines = rawDbDiffOutput.Replace("\r\n", "\n").Split('\n');

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed == UpgradedHeader)
            {
                currentSection = Section.Upgraded;
                continue;
            }

            if (trimmed == AddedHeader)
            {
                currentSection = Section.Added;
                continue;
            }

            if (trimmed == RemovedHeader)
            {
                currentSection = Section.Removed;
                continue;
            }

            // A data line -- how it's parsed depends on which section we're
            // currently inside. Unrecognized lines (section headers we don't
            // know about, or a line that doesn't match the expected shape)
            // are skipped rather than throwing: real-world `db diff` output
            // is not something this app controls, and a single malformed
            // line shouldn't take down the whole diff view (see
            // RpmOstree.md §8.3 on graceful degradation).
            var change = currentSection switch
            {
                Section.Upgraded => TryParseUpgraded(trimmed),
                Section.Added => TryParseAddedOrRemoved(trimmed, PackageChangeKind.Added),
                Section.Removed => TryParseAddedOrRemoved(trimmed, PackageChangeKind.Removed),
                _ => null,
            };

            if (change is not null)
            {
                changes.Add(change);
            }
        }

        return new DeploymentDiff(fromChecksum, toChecksum, changes);
    }

    private static PackageChange? TryParseUpgraded(string line)
    {
        var match = UpgradedLinePattern.Match(line);
        if (!match.Success)
        {
            return null;
        }

        return new PackageChange(
            PackageChangeKind.Upgraded,
            match.Groups["name"].Value,
            OldVersion: match.Groups["old"].Value,
            NewVersion: match.Groups["new"].Value);
    }

    private static PackageChange? TryParseAddedOrRemoved(string line, PackageChangeKind kind)
    {
        var match = AddedOrRemovedLinePattern.Match(line);
        if (!match.Success)
        {
            return null;
        }

        var name = match.Groups["name"].Value;
        var version = match.Groups["version"].Value;

        // Added -> only NewVersion is populated; Removed -> only OldVersion.
        // The other is left null (not empty string) so callers can tell
        // "no version" apart from "version wasn't parsed" -- see
        // PackageChange's doc comment in packageDiff.cs.
        return kind switch
        {
            PackageChangeKind.Added => new PackageChange(kind, name, OldVersion: null, NewVersion: version),
            PackageChangeKind.Removed => new PackageChange(kind, name, OldVersion: version, NewVersion: null),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only Added or Removed are valid here."),
        };
    }
}
