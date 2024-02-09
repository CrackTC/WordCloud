using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace WordCloud;

public partial class WordCloud(
    int width,
    int height,
    SKTypeface? typeface,
    int maxFontSize,
    int minFontSize,
    int fontSizeStep,
    float similiarity,
    int padding,
    int strokeWidth,
    SKColor background,
    SKImage? backgroundImage,
    int backgroudImageBlur,
    Func<string, SKColor>? colorFunc,
    Func<string, SKColor>? strokeColorFunc
) : IDisposable
{
    private readonly Func<string, SKColor> _colorFunc = colorFunc ?? (s => SKColor.Parse("aaffffff"));
    private readonly Func<string, SKColor> _strokeColorFunc = strokeColorFunc ?? (s => SKColors.Black);
    private readonly Random _random = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetIndex(int x, int y) => y * width + x;

    [LibraryImport("wordcloud.so", EntryPoint = "cumulative_sum")]
    private static partial void CumulativeSum([In, Out] int[] arr, int width, int height);

    [LibraryImport("wordcloud.so", EntryPoint = "hit_count")]
    private static partial void HitCount([In, Out] int[] arr, int width, int height, int bw, int bh, [Out] int[] hits);

    private SKPoint? GetTextPosition(SKPixmap pixmap, string text, SKPaint paint, bool vertical)
    {
        var bounds = new SKRect();
        paint.MeasureText(text, ref bounds);
        var (bw, bh) = ((int)Math.Ceiling(bounds.Width + 2 * padding), (int)Math.Ceiling(bounds.Height + 2 * padding));
        if (vertical) (bw, bh) = (bh, bw);

        if (bw >= width || bh >= height) return null;

        int[] matrix = new int[width * height];
        pixmap.GetPixelSpan<int>().CopyTo(matrix);
        CumulativeSum(matrix, width, height);

        int[] hits = new int[height - bh];
        HitCount(matrix, width, height, bw, bh, hits);

        for (int i = 1; i < hits.Length; i++)
            hits[i] += hits[i - 1];
        if (hits[^1] == 0) return null;

        int index = _random.Next(hits[^1]) + 1;
        int row = Array.BinarySearch(hits, index);
        while (row > 0 && hits[row - 1] == hits[row]) row--;
        if (row < 0) row = ~row;
        index -= row == 0 ? 0 : hits[row - 1];

        int count = 0;

        for (int x = 0; x < width - bw; x++)
        {
            if (matrix[GetIndex(x, row)] - matrix[GetIndex(x + bw, row)] + matrix[GetIndex(x + bw, row + bh)] - matrix[GetIndex(x, row + bh)] == 0)
            {
                count++;
                if (count == index)
                {
                    if (!vertical) return new SKPoint(x + padding - bounds.Left, row + padding - bounds.Top);
                    else return new SKPoint(x + padding + bounds.Height + bounds.Top, row + padding - bounds.Left);
                }
            }
        }

        return null;
    }

    private void FillAndStrokeText(SKCanvas canvas, string text, float x, float y, SKPaint paint)
    {
        paint.Style = SKPaintStyle.Fill;
        canvas.DrawText(text, x, y, paint);
        if (strokeWidth == 0) return;

        paint.Style = SKPaintStyle.Stroke;
        paint.Color = _strokeColorFunc(text);
        canvas.DrawText(text, x, y, paint);
    }

    public SKImage GenerateImage(Dictionary<string, int> freqDict)
    {
        var list = freqDict.ToList();
        list.Sort((a, b) => -a.Value.CompareTo(b.Value));

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888);
        using var surface = SKSurface.Create(info);
        using var canvas = surface.Canvas;

        float fontSize = maxFontSize;
        using var paint = new SKPaint { IsAntialias = true };

        for (int i = 0; i < list.Count; i++)
        {
            using var pixmap = surface.PeekPixels();
            var text = list[i].Key;
            bool vertical = _random.Next(2) == 0;

            paint.TextSize = fontSize;
            paint.Color = _colorFunc(text);
            paint.StrokeWidth = strokeWidth;
            if (typeface is { }) paint.Typeface = typeface;

            if (GetTextPosition(pixmap, text, paint, vertical) is { } position)
            {
                if (vertical)
                {
                    canvas.RotateDegrees(90);
                    FillAndStrokeText(canvas, text, position.Y, -position.X, paint);
                    canvas.ResetMatrix();
                }
                else FillAndStrokeText(canvas, text, position.X, position.Y, paint);

                if (i < list.Count - 1)
                {
                    fontSize *= 1 - (1 - (float)list[i + 1].Value / (float)list[i].Value) / similiarity;
                    if (fontSize < minFontSize) break;
                }
            }
            else
            {
                i--;
                fontSize -= fontSizeStep;
                if (fontSize < minFontSize) break;
            }
        }

        using var cloud = surface.Snapshot();
        canvas.Clear(background);

        if (backgroundImage is { } image)
        {
            if (backgroudImageBlur > 0)
            {
                using var filter = SKImageFilter.CreateBlur(backgroudImageBlur, backgroudImageBlur);
                using var blurPaint = new SKPaint { ImageFilter = filter };
                canvas.DrawImage(
                    image,
                    new SKRect(-backgroudImageBlur, -backgroudImageBlur, width + 2 * backgroudImageBlur, height + 2 * backgroudImageBlur),
                    blurPaint
                );
            }
            else canvas.DrawImage(image, new SKPoint(0, 0));
        }

        canvas.DrawImage(cloud, new SKPoint(0, 0));

        return surface.Snapshot();
    }

    public void Dispose()
    {
        backgroundImage?.Dispose();
    }
}

public class WordCloudBuilder
{
    private int _width = 800;
    private int _height = 600;
    private int _maxFontSize = 200;
    private int _minFontSize = 12;
    private SKTypeface? _typeface;
    private int _fontSizeStep = 2;
    private int _padding = 2;
    private SKColor _background = SKColors.Black;
    private SKImage? _backgroundImage;
    private int _backgroudImageBlur = 0;
    private float _similiarity = 5;
    private int _strokeWidth = 0;
    private Func<string, SKColor>? _colorFunc;
    private Func<string, SKColor>? _strokeColorFunc;

    public WordCloudBuilder WithSize(int width, int height)
    {
        _width = width;
        _height = height;
        return this;
    }

    public WordCloudBuilder WithFont(
        string familyName,
        SKFontStyleWeight weight = SKFontStyleWeight.Normal,
        SKFontStyleWidth width = SKFontStyleWidth.Normal,
        SKFontStyleSlant slant = SKFontStyleSlant.Upright
    )
    {
        _typeface = SKTypeface.FromFamilyName(familyName, weight, width, slant);
        return this;
    }

    public WordCloudBuilder WithFontFile(string path, int index = 0)
    {
        _typeface = SKTypeface.FromFile(path, index);
        return this;
    }

    public WordCloudBuilder WithFont(SKTypeface typeface)
    {
        _typeface = typeface;
        return this;
    }

    public WordCloudBuilder WithMaxFontSize(int maxFontSize)
    {
        _maxFontSize = maxFontSize;
        return this;
    }

    public WordCloudBuilder WithMinFontSize(int minFontSize)
    {
        _minFontSize = minFontSize;
        return this;
    }

    public WordCloudBuilder WithFontSizeStep(int fontSizeStep)
    {
        _fontSizeStep = fontSizeStep;
        return this;
    }

    public WordCloudBuilder WithSimiliarity(float similiarity)
    {
        _similiarity = similiarity;
        return this;
    }
    public WordCloudBuilder WithPadding(int padding)
    {
        _padding = padding;
        return this;
    }

    public WordCloudBuilder WithStrokeWidth(int strokeWidth)
    {
        _strokeWidth = strokeWidth;
        return this;
    }

    public WordCloudBuilder WithBackground(SKColor background)
    {
        _background = background;
        return this;
    }

    public WordCloudBuilder WithBackgroundImage(string path)
    {
        _backgroundImage = SKImage.FromEncodedData(File.ReadAllBytes(path));
        return this;
    }

    public WordCloudBuilder WithBackgroundImage(SKImage image)
    {
        _backgroundImage = image;
        return this;
    }

    public WordCloudBuilder WithBlur(int size)
    {
        _backgroudImageBlur = size;
        return this;
    }

    public WordCloudBuilder WithColorFunc(Func<string, SKColor> colorFunc)
    {
        _colorFunc = colorFunc;
        return this;
    }

    public WordCloudBuilder WithStrokeColorFunc(Func<string, SKColor> strokeColorFunc)
    {
        _strokeColorFunc = strokeColorFunc;
        return this;
    }

    public WordCloud Build() => new(
        _width,
        _height,
        _typeface,
        _maxFontSize,
        _minFontSize,
        _fontSizeStep,
        _similiarity,
        _padding,
        _strokeWidth,
        _background,
        _backgroundImage,
        _backgroudImageBlur,
        _colorFunc,
        _strokeColorFunc
    );
}
