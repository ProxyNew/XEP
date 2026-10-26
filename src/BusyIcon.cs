using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace XEnchantmentPatcher;

/// <summary>Small self-contained GIF player for the pixel-art busy indicator.</summary>
public sealed class BusyIcon : System.Windows.Controls.Image
{
    private static readonly Lazy<Frame[]> SharedFrames = new(LoadFrames);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private int _frame;

    public BusyIcon()
    {
        Stretch = Stretch.Uniform;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        _timer.Tick += (_, _) => Advance();
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => { if (IsVisible) Start(); else _timer.Stop(); };
    }

    private sealed record Frame(BitmapSource Image, TimeSpan Delay);

    private static Frame[] LoadFrames()
    {
        var assembly = typeof(BusyIcon).Assembly.GetName().Name;
        var uri = new Uri($"pack://application:,,,/{assembly};component/Assets/iron-helmet-fire-busy.gif", UriKind.Absolute);
        var decoder = new GifBitmapDecoder(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return Composite(decoder);
    }

    // GIF frames after the first are usually partial rectangles at an offset.
    // Showing them raw lets Stretch.Uniform rescale each one differently, so
    // draw every frame onto the full logical screen, honouring disposal.
    private static Frame[] Composite(GifBitmapDecoder decoder)
    {
        var raw = decoder.Frames;
        if (raw.Count == 0) return [];
        int width = Query(decoder.Metadata, "/logscrdesc/Width") ?? raw[0].PixelWidth;
        int height = Query(decoder.Metadata, "/logscrdesc/Height") ?? raw[0].PixelHeight;
        var result = new Frame[raw.Count];
        BitmapSource? canvas = null;
        for (var index = 0; index < raw.Count; index++)
        {
            var frame = raw[index];
            var metadata = frame.Metadata as BitmapMetadata;
            var area = new Rect(Query(metadata, "/imgdesc/Left") ?? 0, Query(metadata, "/imgdesc/Top") ?? 0, frame.PixelWidth, frame.PixelHeight);
            var composed = Render(width, height, canvas, null, frame, area);
            var delay = Query(metadata, "/grctlext/Delay") ?? 0;
            result[index] = new Frame(composed, TimeSpan.FromMilliseconds(delay > 0 ? delay * 10 : 100));
            canvas = (Query(metadata, "/grctlext/Disposal") ?? 0) switch
            {
                2 => Render(width, height, composed, area, null, area),
                3 => canvas,
                _ => composed,
            };
        }
        return result;
    }

    private static BitmapSource Render(int width, int height, BitmapSource? background, Rect? clear, BitmapSource? frame, Rect area)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            if (background is not null)
            {
                if (clear is { } hole)
                    context.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, width, height)), new RectangleGeometry(hole)));
                context.DrawImage(background, new Rect(0, 0, width, height));
                if (clear is not null) context.Pop();
            }
            if (frame is not null) context.DrawImage(frame, area);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static int? Query(BitmapMetadata? metadata, string query)
    {
        try { return metadata?.GetQuery(query) is { } value ? Convert.ToInt32(value) : null; }
        catch (NotSupportedException) { return null; }
    }

    private void Start()
    {
        var frames = SharedFrames.Value;
        if (frames.Length == 0) return;
        _frame = 0;
        Show(frames[0]);
        if (IsVisible) _timer.Start();
    }

    private void Advance()
    {
        var frames = SharedFrames.Value;
        if (frames.Length == 0) return;
        _frame = (_frame + 1) % frames.Length;
        Show(frames[_frame]);
    }

    private void Show(Frame frame)
    {
        Source = frame.Image;
        _timer.Interval = frame.Delay;
    }
}
