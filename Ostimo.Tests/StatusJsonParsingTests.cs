using System.Text.Json;

namespace Ostimo.Tests;

using FluentAssertions;
using Ostimo.Core.Models;

/// <summary>
/// Tests for deserializing captured <c>rpm-ostree status --json</c> output
/// into <see cref="StatusResult"/>.
///
/// Per RpmOstree.md §10, fixtures here are modeled closely on the real
/// Aurora DX capture in §5.1 (including every field that capture had, not
/// just the ones <see cref="Deployment"/> maps) rather than hand-abbreviated
/// JSON, and specifically cover the edge cases called out there: zero
/// deployments, a deployment with a non-empty <c>requested-packages</c>
/// list, and a non-null <c>transaction</c>.
/// </summary>
public sealed class StatusJsonParsingTests
{
    [Fact]
    public void Deserialize_RealisticMultiDeploymentStatus_ParsesAllFieldsCorrectly()
    {
        var json = ReadSampleData("status-sample-1.json");

        var result = JsonSerializer.Deserialize<StatusResult>(json);

        result.Should().NotBeNull();
        result!.Deployments.Should().HaveCount(3);
        result.TransactionInProgress.Should().BeFalse();

        var booted = result.Deployments[0];
        booted.Id.Should().Be("default-384892929bb2bd7a61ec17470c4f7adf9ffba309505a1de52927d99ccc6fcdff.0");
        booted.OsName.Should().Be("default");
        booted.Serial.Should().Be(0);
        booted.Checksum.Should().Be("384892929bb2bd7a61ec17470c4f7adf9ffba309505a1de52927d99ccc6fcdff");
        booted.Version.Should().Be("44.20260922.1");
        booted.Booted.Should().BeTrue();
        booted.Pinned.Should().BeFalse();
        booted.Staged.Should().BeFalse();

        // 1790040916 confirmed as *seconds* in RpmOstree.md §5.1 -- verify
        // the converter didn't accidentally treat it as milliseconds, which
        // would land in 1970 rather than 2026.
        booted.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1790040916));
        booted.Timestamp.Year.Should().Be(2026);

        booted.ContainerImageReference.Should().Be("ostree-image-signed:docker://ghcr.io/ublue-os/aurora-dx:stable");
        booted.ContainerImageReferenceDigest.Should().Be("sha256:bb406b6675942516e8f223a36a9c99969070311e09060784d8108cbe1bb82c6d");

        // There is deliberately no "origin" or "gpg-enabled" field on Aurora
        // (RpmOstree.md §5.1's warning) -- ImageShortName/ImageTag are the
        // model's own derived accessors, parsed from container-image-reference.
        booted.ImageShortName.Should().Be("aurora-dx");
        booted.ImageTag.Should().Be("stable");

        booted.RequestedPackages.Should().BeEmpty();
    }

    [Fact]
    public void Deserialize_ExactlyOneDeploymentIsBooted_BootedAccessorFindsIt()
    {
        var json = ReadSampleData("status-sample-1.json");

        var result = JsonSerializer.Deserialize<StatusResult>(json);

        result!.Booted.Should().NotBeNull();
        result.Booted!.Version.Should().Be("44.20260922.1");

        // Exactly one, per the rpm-ostree contract (StatusResult.cs's doc
        // comment) -- verify the other two are correctly *not* flagged
        // booted, not just that Booted found *a* match.
        result.Deployments.Count(d => d.Booted).Should().Be(1);
    }

    [Fact]
    public void Deserialize_PinnedDeployment_IsFlaggedPinned()
    {
        var json = ReadSampleData("status-sample-1.json");

        var result = JsonSerializer.Deserialize<StatusResult>(json);

        var pinned = result!.Deployments.Single(d => d.Version == "44.20260830.0");
        pinned.Pinned.Should().BeTrue();
    }

    [Fact]
    public void Deserialize_DeploymentWithRequestedPackages_PopulatesTheList()
    {
        // Edge case explicitly called out in RpmOstree.md §10: a deployment
        // with a non-empty requested-packages array.
        var json = ReadSampleData("status-sample-1.json");

        var result = JsonSerializer.Deserialize<StatusResult>(json);

        var withRequestedPackages = result!.Deployments.Single(d => d.Version == "44.20260830.0");
        withRequestedPackages.RequestedPackages.Should().BeEquivalentTo(["distrobox", "fastfetch"]);
    }

    [Fact]
    public void Deserialize_ZeroDeployments_ProducesEmptyListNotNull()
    {
        // Edge case explicitly called out in RpmOstree.md §10: "shouldn't
        // happen but don't trust that". StatusResult.Booted must degrade to
        // null gracefully rather than throwing when there's nothing to find.
        var json = ReadSampleData("status-sample-2-zero-deployments.json");

        var result = JsonSerializer.Deserialize<StatusResult>(json);

        result.Should().NotBeNull();
        result!.Deployments.Should().NotBeNull();
        result.Deployments.Should().BeEmpty();
        result.Booted.Should().BeNull();
        result.TransactionInProgress.Should().BeFalse();
    }

    [Fact]
    public void Deserialize_NonNullTransaction_TransactionInProgressIsTrue()
    {
        // Edge case explicitly called out in RpmOstree.md §10: a
        // non-null "transaction". StatusResult.TransactionInProgress
        // flattens the raw object/null into a bool per its doc comment --
        // verify that flattening actually happens correctly in both
        // directions (this test) and the null case (covered by every other
        // fixture, which all have "transaction": null).
        var json = ReadSampleData("status-sample-3-transaction-in-progress.json");

        var result = JsonSerializer.Deserialize<StatusResult>(json);

        result!.TransactionInProgress.Should().BeTrue();
        result.RawTransaction.Should().NotBeNull();
    }

    [Fact]
    public void Deserialize_StagedDeployment_IsFlaggedStagedAndNotBooted()
    {
        // Staged is documented as worth surfacing as its own visual state
        // (Deployment.cs's doc comment) rather than folding into "just
        // another deployment" -- verify staged and booted are tracked
        // independently, since a staged deployment is specifically *not*
        // yet booted.
        var json = ReadSampleData("status-sample-4-staged-deployment.json");

        var result = JsonSerializer.Deserialize<StatusResult>(json);

        var staged = result!.Deployments.Single(d => d.Version == "44.20260927.0");
        staged.Staged.Should().BeTrue();
        staged.Booted.Should().BeFalse();

        result.Booted.Should().NotBeNull();
        result.Booted!.Staged.Should().BeFalse();
        result.Booted.Version.Should().Be("44.20260922.1");
    }

    [Theory]
    [InlineData("ostree-image-signed:docker://ghcr.io/ublue-os/aurora-dx:stable", "aurora-dx", "stable")]
    [InlineData("ostree-image-signed:docker://ghcr.io/ublue-os/bazzite:latest", "bazzite", "latest")]
    [InlineData("not-a-recognizable-reference", "not-a-recognizable-reference", null)]
    public void ImageShortNameAndImageTag_ParseVariousReferenceShapes(
        string containerImageReference, string expectedShortName, string? expectedTag)
    {
        // ImageShortName/ImageTag are the deliberate replacement for the
        // "osname" field, which RpmOstree.md §5.1 confirms is just "default"
        // on Aurora and can't be used to distinguish distros -- these
        // accessors are what the UI/timeline should actually use instead.
        var deployment = new Deployment
        {
            Id = "test-id",
            OsName = "default",
            Serial = 0,
            Checksum = "deadbeef",
            Version = "1.0.0",
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(0),
            ContainerImageReference = containerImageReference,
            Booted = false,
            Pinned = false,
            Staged = false,
        };

        deployment.ImageShortName.Should().Be(expectedShortName);
        deployment.ImageTag.Should().Be(expectedTag);
    }

    private static string ReadSampleData(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SampleData", fileName));
}