using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ScreenshotDock.App.ViewModels;
using ScreenshotDock.App.Views;
using ScreenshotDock.Core.Services;
using ScreenshotDock.Windows.Capture;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.IntegrationTests;

internal static partial class Program
{
    // Render only our own windows and synthetic pictures for visual review.
    private static async Task RenderThemePreviewAsync()
    {
        var repository = new ScreenshotRepository(Path.Combine(_output, "theme-fixtures"));
        repository.Load(new(), DateTimeOffset.UtcNow);
        repository.Clear();
        var cards = new List<ScreenshotCard>();
        foreach (var size in new[] { (600, 360), (340, 520), (560, 320) })
        {
            var image = EditorFixture(size.Item1, size.Item2);
            var added = repository.Add(ScreenCaptureService.EncodePng(image), ScreenCaptureService.Thumbnail(image), new(), DateTimeOffset.UtcNow);
            cards.Add(new(added.Item, repository));
        }

        void ShowForPreview(Window window)
        {
            window.ShowActivated = false;
            window.IsHitTestVisible = false;
            DesktopEnvironment.DoNotActivate(window);
            DesktopEnvironment.MakeClickThrough(window);
            window.Show();
        }

        var settings = new SettingsWindow(new(), _ => Task.FromResult<string?>(null));
        try { ShowForPreview(settings); await Task.Delay(60); SaveVisual(settings, "settings-blue.png"); }
        finally { settings.Close(); }
        var preview = new PreviewWindow(cards[0]);
        try { ShowForPreview(preview); await Task.Delay(60); SaveVisual(preview, "preview-blue.png"); }
        finally { preview.Close(); }
        var edge = new EdgeWindow();
        try { edge.PositionAtTop(); ShowForPreview(edge); await Task.Delay(30); SaveVisual(edge, "edge-blue.png"); }
        finally { edge.Close(); }

        var dock = new DockWindow { IsHitTestVisible = false };
        try
        {
            dock.Configure(new()); dock.SetCards(cards); dock.SetLocked(true);
            DesktopEnvironment.MakeClickThrough(dock);
            dock.Expand(TimeSpan.FromSeconds(2));
            await Task.Delay(500);
            dock.ShowCopied(cards[1].Item.Id);
            var shelf = SaveVisual(dock, "dock-blue.png");
            var shadow = SaveVisual(_application.Windows.OfType<DockBackdropWindow>().Single(), "dock-shadow-blue.png");
            SaveShelfComposition(shelf, shadow, "dock-blue-composited.png");
            dock.SetCards([]);
            await Task.Delay(50);
            var emptyShelf = SaveVisual(dock, "dock-empty-blue.png");
            SaveShelfComposition(emptyShelf, shadow, "dock-empty-blue-composited.png");
        }
        finally { dock.Close(); }
    }
}
