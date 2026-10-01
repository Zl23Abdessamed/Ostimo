using System.Text.Json.Serialization;

namespace Ostimo.Core.Models;

// Note: ImageShortName/ImageTag below use System.Linq (LastOrDefault, FirstOrDefault).
// Assumes <ImplicitUsings>enable</ImplicitUsings> in the .csproj; add an explicit
// `using System.Linq;` if that's not set.

/// <summary>
/// A single rpm-ostree deployment, as reported by <c>rpm-ostree status --json</c>.
///
/// Field set and naming verified against real output captured on Aurora DX
/// (44.20260922.1, bootc-based). This is an image-based / container-native
/// rpm-ostree system, which differs in a few important ways from the classic
/// RPM-server model most rpm-ostree tutorials describe:
///
///   - There is no "origin" field. The image reference lives in
///     <see cref="ContainerImageReference"/> instead, e.g.
///     "ostree-image-signed:docker://ghcr.io/ublue-os/aurora-dx:stable".
///   - "osname" is just "default" on Aurora, not distro-specific. Don't use it
///     to tell Aurora apart from Aurora DX, Bazzite, etc. -- parse
///     <see cref="ImageShortName"/> instead.
///   - There is no "gpg-enabled" field. Signature verification is implied by
///     the "ostree-image-signed:" prefix on the image reference (sigstore-based
///     container signing), not a classic per-deployment GPG flag.
///
/// See RpmOstree.md §5.1 for the full annotated sample JSON this was modeled
/// against, including the raw field-by-field notes.
/// </summary>
public sealed record Deployment
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("osname")]
    public required string OsName { get; init; }

    [JsonPropertyName("serial")]
    public required int Serial { get; init; }

    [JsonPropertyName("checksum")]
    public required string Checksum { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>
    /// Build/commit time. rpm-ostree reports this as a Unix timestamp in
    /// *seconds* (confirmed from real data, e.g. 1790040916) -- watch for the
    /// classic seconds-vs-milliseconds mixup when converting.
    /// </summary>
    [JsonPropertyName("timestamp")]
    [JsonConverter(typeof(UnixSecondsToDateTimeOffsetConverter))]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// The image this deployment came from, e.g.
    /// "ostree-image-signed:docker://ghcr.io/ublue-os/aurora-dx:stable".
    /// This is what "origin" would have been called on a classic rpm-ostree
    /// system -- Aurora/bootc systems use this field instead.
    /// </summary>
    [JsonPropertyName("container-image-reference")]
    public required string ContainerImageReference { get; init; }

    /// <summary>
    /// The exact digest of the pulled image, e.g. "sha256:bb406b66...".
    /// This is the closest thing to a provenance/signature identifier on an
    /// image-based system -- there is no separate GPG signature field.
    /// </summary>
    [JsonPropertyName("container-image-reference-digest")]
    public string? ContainerImageReferenceDigest { get; init; }

    [JsonPropertyName("booted")]
    public required bool Booted { get; init; }

    [JsonPropertyName("pinned")]
    public required bool Pinned { get; init; }

    /// <summary>
    /// True if this deployment is queued but not yet finalized -- i.e. an
    /// update has been pulled and will take effect on the next reboot, but
    /// hasn't yet. Worth surfacing as its own visual state in the UI rather
    /// than folding it into "just another deployment".
    /// </summary>
    [JsonPropertyName("staged")]
    public required bool Staged { get; init; }

    [JsonPropertyName("regenerate-initramfs")]
    public bool RegenerateInitramfs { get; init; }

    [JsonPropertyName("requested-packages")]
    public IReadOnlyList<string> RequestedPackages { get; init; } = [];

    /// <summary>
    /// The full container image config / build-history blob, deliberately
    /// left untyped. On Aurora this is tens of KB per deployment (a doubly
    /// JSON-escaped string containing the OCI image config plus a
    /// package-by-package build history from Aurora's "chunkah" tooling).
    /// It's genuinely useful as an optional, lazily-inspected detail (e.g. a
    /// "view build provenance" panel) but should never be eagerly parsed into
    /// strongly-typed C# records -- it's deeply nested, versioned by OCI spec
    /// conventions rather than rpm-ostree's, and out of scope for v1.
    /// </summary>
    [JsonPropertyName("base-commit-meta")]
    public Dictionary<string, object?>? BaseCommitMeta { get; init; }

    /// <summary>
    /// Pulls the human-readable image name out of <see cref="ContainerImageReference"/>.
    /// e.g. "ostree-image-signed:docker://ghcr.io/ublue-os/aurora-dx:stable" -> "aurora-dx".
    /// Falls back to the raw reference if the expected shape isn't found.
    /// </summary>
    [JsonIgnore]
    public string ImageShortName
    {
        get
        {
            var afterLastSlash = ContainerImageReference.Split('/').LastOrDefault();
            if (string.IsNullOrEmpty(afterLastSlash))
            {
                return ContainerImageReference;
            }

            var beforeColon = afterLastSlash.Split(':').FirstOrDefault();
            return string.IsNullOrEmpty(beforeColon) ? ContainerImageReference : beforeColon;
        }
    }

    /// <summary>
    /// The tag portion of <see cref="ContainerImageReference"/>, e.g. "stable"
    /// or "latest". Null if it can't be confidently parsed out.
    /// </summary>
    [JsonIgnore]
    public string? ImageTag
    {
        get
        {
            var afterLastSlash = ContainerImageReference.Split('/').LastOrDefault();
            var parts = afterLastSlash?.Split(':');
            return parts is { Length: > 1 } ? parts[^1] : null;
        }
    }
}

/// <summary>
/// rpm-ostree reports deployment timestamps as Unix seconds (a plain integer),
/// not the ISO-8601 string <see cref="DateTimeOffset"/> expects by default --
/// this converter bridges that.
/// </summary>
public sealed class UnixSecondsToDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(
        ref System.Text.Json.Utf8JsonReader reader,
        Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options)
    {
        var seconds = reader.GetInt64();
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    public override void Write(
        System.Text.Json.Utf8JsonWriter writer,
        DateTimeOffset value,
        System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.ToUnixTimeSeconds());
    }
}