using CommunityToolkit.Mvvm.ComponentModel;

namespace ExplorerHelper.Models;

/// <summary>
/// One label/value pair shown in the preview details panel (e.g. "Resolution" → "1920 × 1080").
/// Rows are only produced for details the user has enabled and that actually have a value.
/// </summary>
public sealed record PreviewDetailRow(string Label, string Value);

/// <summary>
/// A toggle in Settings controlling whether one kind of detail appears under the preview
/// (issue #20). The key matches <see cref="PreviewDetailKinds"/>; the label is what the user sees.
/// </summary>
public partial class PreviewDetailToggle : ObservableObject
{
    public string Key { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isChecked;

    public PreviewDetailToggle(string key, string label, bool isChecked)
    {
        Key = key;
        Label = label;
        _isChecked = isChecked;
    }
}

/// <summary>
/// The catalogue of detail types the preview can show, in display order. Media-specific rows
/// (resolution, length, frame rate, bit rate) only appear when the selected file actually has
/// that metadata; the always-available ones fall back to the file system.
/// </summary>
public static class PreviewDetailKinds
{
    public const string Type = "Type";
    public const string Size = "Size";
    public const string Dimensions = "Dimensions";
    public const string Duration = "Duration";
    public const string FrameRate = "FrameRate";
    public const string Bitrate = "Bitrate";
    public const string Created = "Created";
    public const string Modified = "Modified";

    /// <summary>Every detail type, paired with its user-facing label, in the order shown.</summary>
    public static readonly (string Key, string Label)[] All =
    [
        (Type, "Type"),
        (Size, "Size"),
        (Dimensions, "Resolution"),
        (Duration, "Length"),
        (FrameRate, "Frame rate"),
        (Bitrate, "Bit rate"),
        (Created, "Date created"),
        (Modified, "Date modified"),
    ];

    /// <summary>Detail types shown by default (frame rate and bit rate start off — niche).</summary>
    public static readonly string[] DefaultEnabled =
        [Type, Size, Dimensions, Duration, Created, Modified];
}
