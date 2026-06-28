using System;

namespace ACT_DiscordTriggers.Core.Update {
  /// <summary>
  /// The result of an update check against the GitHub Releases API: the latest published
  /// (non-prerelease) release's version, its plain-text notes, and the URL of the release
  /// archive asset to download. UI-agnostic — produced by <see cref="GithubReleaseClient"/>
  /// and consumed by the update service / ViewModel.
  /// </summary>
  public sealed class UpdateInfo {
    /// <summary>The release's version, parsed from its <c>tag_name</c> (leading "v" stripped).</summary>
    public Version Version { get; set; }

    /// <summary>The raw release tag (e.g. <c>v2.2.0</c>), kept for display/links.</summary>
    public string TagName { get; set; }

    /// <summary>The release title (GitHub <c>name</c>), or the tag when absent.</summary>
    public string Name { get; set; }

    /// <summary>The release body as plain text (Markdown source, shown as-is — no HTML).</summary>
    public string Notes { get; set; }

    /// <summary>Direct download URL of the release archive asset (the <c>.zip</c>).</summary>
    public string DownloadUrl { get; set; }

    /// <summary>The GitHub release page URL, for the "view release" fallback link.</summary>
    public string HtmlUrl { get; set; }

    /// <summary>True when <see cref="Version"/> is newer than the running plugin version.</summary>
    public bool IsNewer { get; set; }
  }
}
