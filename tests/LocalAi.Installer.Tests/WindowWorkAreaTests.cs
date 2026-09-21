using System.Windows;

namespace LocalAi.Installer.Tests;

public sealed class WindowWorkAreaTests
{
    [Theory]
    [InlineData(1366, 728, 96)]
    [InlineData(1366, 708, 144)]
    [InlineData(1920, 1040, 96)]
    [InlineData(1920, 1020, 144)]
    [InlineData(900, 660, 144)]
    public async Task A_window_fits_the_work_area_and_can_resize_again_on_a_larger_monitor(int width, int height, int dpi)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var selection = new object();
                var window = new Window { Width = 820, Height = 620, MinWidth = 780, MinHeight = 580, DataContext = selection };
                try
                {
                    var font = window.FontSize;
                    WindowWorkArea.Apply(window, new Size(width, height), dpi, new Size(780, 580));
                    var availableWidth = width * 96d / dpi;
                    var availableHeight = height * 96d / dpi;
                    Assert.InRange(window.Width, 1, availableWidth);
                    Assert.InRange(window.Height, 1, availableHeight);
                    Assert.InRange(window.MinWidth, 1, availableWidth);
                    Assert.InRange(window.MinHeight, 1, availableHeight);
                    Assert.InRange(window.MaxWidth, 1, availableWidth);
                    Assert.InRange(window.MaxHeight, 1, availableHeight);
                    Assert.Same(selection, window.DataContext);
                    Assert.Equal(font, window.FontSize);

                    // A previous small monitor must not permanently pin the maximum or minimum.
                    WindowWorkArea.Apply(window, new Size(2560, 1400), 96, new Size(780, 580));
                    window.Width = 1200;
                    window.Height = 900;
                    Assert.Equal(1200, window.Width);
                    Assert.Equal(900, window.Height);
                    Assert.Equal(780, window.MinWidth);
                    Assert.Equal(580, window.MinHeight);
                    Assert.True(window.MaxWidth >= window.Width && window.MaxHeight >= window.Height);
                }
                finally { window.Close(); }
                completed.SetResult();
            }
            catch (Exception exception) { completed.SetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Moving_to_a_smaller_monitor_keeps_the_caption_and_bottom_inside_its_work_area()
    {
        var work = new Rect(-1366, 60, 1366, 708); // secondary monitor to the left, taskbar at the top
        var fitted = WindowWorkArea.FitBounds(new Rect(-1200, -10, 1230, 930), work);
        Assert.True(work.Contains(fitted));
        Assert.Equal(708, fitted.Height);
        Assert.Equal(1230, fitted.Width);
        Assert.Equal(fitted, WindowWorkArea.FitBounds(fitted, work));
    }
}
