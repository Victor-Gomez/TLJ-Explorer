using Avalonia;

namespace TLJExplorer.Services;

/// <summary>
/// Validation for restored window bounds. Kept separate from the window itself so the "would this land
/// somewhere the user can actually reach it" rule is unit-testable: a saved position is only as good as
/// the display layout it was saved under, and monitors get unplugged, rearranged and resized between
/// sessions.
/// </summary>
public static class WindowGeometry
{
    /// <summary>
    /// How much of the window has to overlap a screen for the restore to count as usable. Roughly a
    /// grabbable strip of title bar -- enough for the user to drag the window back into view.
    /// </summary>
    public const int MinVisibleWidth = 120;

    /// <inheritdoc cref="MinVisibleWidth"/>
    public const int MinVisibleHeight = 40;

    /// <summary>Smallest window we'll restore to; anything less is treated as corrupt and ignored.</summary>
    public const int MinWindowWidth = 400;

    /// <inheritdoc cref="MinWindowWidth"/>
    public const int MinWindowHeight = 300;

    /// <summary>
    /// Whether <paramref name="window"/> is worth restoring: a sane size, and enough of it landing on one
    /// of <paramref name="screens"/> to be seen and grabbed. A window restored onto a monitor that is no
    /// longer attached is invisible and, on Windows, effectively unrecoverable without editing settings
    /// by hand -- so the caller falls back to centring instead.
    /// </summary>
    public static bool IsRestorable(PixelRect window, IReadOnlyList<PixelRect> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);

        if (window.Width < MinWindowWidth || window.Height < MinWindowHeight)
            return false;

        foreach (PixelRect screen in screens)
        {
            PixelRect overlap = screen.Intersect(window);
            if (overlap.Width >= MinVisibleWidth && overlap.Height >= MinVisibleHeight)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Builds a candidate rect from persisted settings, or <see langword="null"/> when any component is
    /// missing (nothing saved yet, or a partially hand-edited settings file).
    /// </summary>
    public static PixelRect? FromSettings(int? x, int? y, double? width, double? height)
    {
        if (x is null || y is null || width is null || height is null)
            return null;

        // Guard the cast: a NaN or absurd value in settings.json would otherwise become a garbage rect.
        if (double.IsNaN(width.Value) || double.IsNaN(height.Value) ||
            width.Value <= 0 || height.Value <= 0 ||
            width.Value > int.MaxValue || height.Value > int.MaxValue)
        {
            return null;
        }

        return new PixelRect(x.Value, y.Value, (int)width.Value, (int)height.Value);
    }
}
