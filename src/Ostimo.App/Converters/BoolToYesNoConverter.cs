using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Ostimo.App.Converters;

/// <summary>
/// Renders a bool as "Yes"/"No" text -- used for the Pinned/Staged rows in
/// the detail pane's metadata table (OstimoUi.md §3.2). Kept as a tiny
/// UI-only converter in Ostimo.App rather than a formatted string property
/// on <c>DeploymentViewModel</c>, since "Yes"/"No" is presentation, not
/// domain data the ViewModel itself needs to reason about.
/// </summary>
public sealed class BoolToYesNoConverter : IValueConverter
{
    public static readonly BoolToYesNoConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Yes" : "No";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(BoolToYesNoConverter)} only supports one-way binding.");
}
