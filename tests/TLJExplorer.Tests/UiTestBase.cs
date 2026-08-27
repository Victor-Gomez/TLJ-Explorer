using Avalonia.Controls;

namespace TLJExplorer.Tests;

/// <summary>
/// Base for tests that construct real windows. Every window opened through <see cref="Track{T}"/> is
/// closed when the test finishes: headless tests share one Application, and windows left open leak into
/// later tests, which then fail nondeterministically depending on run order (typically surfacing as
/// "The given key 'fonts:SystemFonts' was not present in the dictionary" once the shared platform is
/// torn down underneath them).
/// </summary>
public abstract class UiTestBase : IDisposable
{
    private readonly List<Window> _windows = [];

    protected T Track<T>(T window) where T : Window
    {
        _windows.Add(window);
        return window;
    }

    /// <summary>Constructs, tracks and shows a window in one step.</summary>
    protected T Show<T>(T window) where T : Window
    {
        Track(window);
        window.Show();
        return window;
    }

    public void Dispose()
    {
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            try
            {
                _windows[i].Close();
            }
            catch (Exception)
            {
                // A window that already closed (or never fully opened) must not fail the test run.
            }
        }

        _windows.Clear();
        GC.SuppressFinalize(this);
    }
}
