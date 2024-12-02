using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using HarfBuzzSharp;
using NeoSmart.Unicode;

namespace WordCloud;

// https://www.mrumpler.at/the-trouble-with-text-rendering-in-skiasharp-and-harfbuzz/
internal class HarfBuzzMeasuring : IDisposable
{
    private readonly Blob blob;
    private readonly Face face;
    private readonly Font font;
    private readonly HarfBuzzSharp.Buffer buffer = new();

    private readonly int xScale, yScale;

    public HarfBuzzMeasuring(SKTypeface skface)
    {
        blob = skface.OpenStream().ToHarfBuzzBlob();
        face = new Face(blob, 0) { UnitsPerEm = skface.UnitsPerEm };
        font = new Font(face);
        font.GetScale(out xScale, out yScale);
    }

    public void Dispose()
    {
        font.Dispose();
        face.Dispose();
        blob.Dispose();
        buffer.Dispose();
    }

    public (float, float) MeasureText(string text, float fontSize)
    {
        buffer.ClearContents();
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        font.Shape(buffer);

        var width = buffer.GlyphPositions.Sum(pos => pos.XAdvance) * fontSize / xScale;
        var height = face.UnitsPerEm * fontSize / yScale;

        return (width, height);
    }
}

public partial class WordCloud(
    int width,
    int height,
    SKTypeface? typeface,
    SKTypeface? emojiTypeface,
    int maxFontSize,
    int minFontSize,
    int fontSizeStep,
    float similarity,
    int padding,
    int strokeWidth,
    float strokeRatio,
    SKColor background,
    SKImage? backgroundImage,
    int backgroundImageBlur,
    SKImage? mask,
    float verticality,
    Func<string, SKColor>? colorFunc,
    Func<string, SKColor>? strokeColorFunc
) : IDisposable
{
    private readonly Func<string, SKColor> _colorFunc = colorFunc ?? (s => SKColor.Parse("aaffffff"));
    private readonly Func<string, SKColor> _strokeColorFunc = strokeColorFunc ?? (s => SKColors.Black);
    private readonly int[] _matrix = new int[width * height];
    private readonly SKShaper _shaper = new SKShaper(typeface ?? SKTypeface.Default);
    private readonly SKShaper _emojiShaper = new SKShaper(emojiTypeface ?? SKTypeface.Default);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetIndex(int x, int y) => y * width + x;

    [LibraryImport("wordcloud", EntryPoint = "cumulative_sum")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSuppressGCTransition)])]
    private static partial void CumulativeSum([In, Out] int[] arr, int width, int height);

    [LibraryImport("wordcloud", EntryPoint = "hit_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSuppressGCTransition)])]
    private static partial void HitCount([In, Out] int[] arr, int width, int height, int bw, int bh, [Out] int[] hits);

    private SKPoint? GetTextPosition(SKPixmap pixmap, float blockWidth, float blockHeight, bool vertical)
    {
        var (bw, bh) = ((int)Math.Ceiling(blockWidth + 2 * padding), (int)Math.Ceiling(blockHeight * 1.2f + 2 * padding));
        if (vertical) (bw, bh) = (bh, bw);

        if (bw >= width || bh >= height) return null;

        pixmap.GetPixelSpan<int>().CopyTo(_matrix);
        CumulativeSum(_matrix, width, height);

        var hits = ArrayPool<int>.Shared.Rent(height - bh);
        HitCount(_matrix, width, height, bw, bh, hits);
        var hitSpan = hits.AsSpan(0, height - bh);

        if (hitSpan[^1] == 0) return null;

        int index = Random.Shared.Next(hitSpan[^1]) + 1;
        int row = hitSpan.BinarySearch(index);
        while (row > 0 && hitSpan[row - 1] == hitSpan[row]) row--;
        if (row < 0) row = ~row;
        index -= row == 0 ? 0 : hitSpan[row - 1];

        int count = 0;

        for (int x = 0; x < width - bw; x++)
        {
            if (_matrix[GetIndex(x, row)] - _matrix[GetIndex(x + bw, row)] + _matrix[GetIndex(x + bw, row + bh)] - _matrix[GetIndex(x, row + bh)] == 0)
            {
                count++;
                if (count == index)
                {
                    ArrayPool<int>.Shared.Return(hits);
                    if (!vertical) return new(x + padding, row + padding + blockHeight);
                    else return new(x + padding + blockHeight * 0.2f, row + padding);
                }
            }
        }

        ArrayPool<int>.Shared.Return(hits);
        return null;
    }

    private void FillText(SKCanvas canvas, string text, float x, float y, SKShaper shaper, SKPaint paint)
    {
        paint.Style = SKPaintStyle.Fill;
        canvas.DrawShapedText(shaper, text, x, y, paint);
    }

    private void StrokeText(SKCanvas canvas, string text, float x, float y, SKShaper shaper, SKPaint paint)
    {
        paint.Style = SKPaintStyle.Stroke;
        paint.Color = _strokeColorFunc(text);

        if (strokeRatio > 0)
            paint.StrokeWidth = paint.TextSize * strokeRatio;
        else
            paint.StrokeWidth = strokeWidth;
        canvas.DrawShapedText(shaper, text, x, y, paint);
    }

    private void FillAndStrokeText(SKCanvas canvas, string text, bool isEmoji, float x, float y, SKShaper shaper, SKPaint paint)
    {
        FillText(canvas, text, x, y, shaper, paint);
        if ((strokeWidth > 0 || strokeRatio > 0.0f) && !isEmoji)
            StrokeText(canvas, text, x, y, shaper, paint);
    }

    private void DrawText(SKCanvas canvas, string text, bool isEmoji, SKPoint position, SKShaper shaper, SKPaint paint, bool vertical)
    {
        canvas.Save();
        if (vertical)
        {
            canvas.RotateDegrees(90);
            FillAndStrokeText(canvas, text, isEmoji, position.Y, -position.X, shaper, paint);
        }
        else FillAndStrokeText(canvas, text, isEmoji, position.X, position.Y, shaper, paint);
        canvas.Restore();
    }

    private void DrawBackground(SKCanvas canvas)
    {
        using var paint = new SKPaint { Color = background, BlendMode = SKBlendMode.DstOver };
        var dest = new SKRect(-backgroundImageBlur, -backgroundImageBlur, width + 2 * backgroundImageBlur, height + 2 * backgroundImageBlur);

        if (backgroundImage is null)
        {
            canvas.DrawRect(dest, paint);
            return;
        }

        if (backgroundImageBlur > 0)
            paint.ImageFilter = SKImageFilter.CreateBlur(backgroundImageBlur, backgroundImageBlur);

        canvas.DrawImage(backgroundImage, dest, paint);
    }

    private void DrawMask(SKCanvas canvas)
    {
        if (mask is null) return;
        canvas.DrawImage(mask, new SKRect(0, 0, width, height));
    }

    private void ClearMask(SKCanvas canvas)
    {
        if (mask is null) return;
        using var paint = new SKPaint { BlendMode = SKBlendMode.DstOut };
        canvas.DrawImage(mask, new SKRect(0, 0, width, height), paint);
    }

    public SKImage GenerateImage(Dictionary<string, int> freqDict)
    {
        var list = freqDict.OrderByDescending(x => x.Value)
                           .Select(x => (
                               Text: x.Key,
                               Freq: x.Value,
                               Vertical: Random.Shared.NextSingle() < verticality,
                               IsEmoji: Emoji.IsEmoji(x.Key)
                            ))
                           .ToList();

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888);
        using var surface = SKSurface.Create(info);
        using var canvas = surface.Canvas;

        DrawMask(canvas);

        float fontSize = maxFontSize;
        using var paint = new SKPaint { IsAntialias = true };
        using var measuring = new HarfBuzzMeasuring(typeface ?? SKTypeface.Default);
        using var emojiMeasuring = new HarfBuzzMeasuring(emojiTypeface ?? SKTypeface.Default);

        for (int i = 0; i < list.Count && fontSize >= minFontSize; i++)
        {
            using var pixmap = surface.PeekPixels();
            var (text, freq, vertical, isEmoji) = list[i];

            paint.TextSize = fontSize;
            paint.Color = _colorFunc(text);

            paint.Typeface = isEmoji ? emojiTypeface : typeface;

            var (w, h) = (isEmoji ? emojiMeasuring : measuring).MeasureText(text, fontSize);

            if (GetTextPosition(pixmap, w, h, vertical) is { } position)
            {
                DrawText(canvas, text, isEmoji, position, isEmoji ? _emojiShaper : _shaper, paint, vertical);
                if (i < list.Count - 1)
                    fontSize *= 1 - (1 - (float)list[i + 1].Freq / (float)freq) / similarity;
            }
            else
            {
                i--;
                fontSize -= fontSizeStep;
            }
        }

        ClearMask(canvas);

        DrawBackground(canvas);
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
    private SKTypeface? _emojiTypeface;
    private int _fontSizeStep = 2;
    private int _padding = 2;
    private SKColor _background = SKColors.Black;
    private SKImage? _backgroundImage;
    private int _backgroundImageBlur = 0;
    private SKImage? _mask;
    private float _verticality = 0.3f;
    private float _similarity = 5;
    private int _strokeWidth = 0;
    private float _strokeRatio = 0;
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

    public WordCloudBuilder WithEmojiFont(
        string familyName,
        SKFontStyleWeight weight = SKFontStyleWeight.Normal,
        SKFontStyleWidth width = SKFontStyleWidth.Normal,
        SKFontStyleSlant slant = SKFontStyleSlant.Upright
    )
    {
        _emojiTypeface = SKTypeface.FromFamilyName(familyName, weight, width, slant);
        return this;
    }

    public WordCloudBuilder WithEmojiFontFile(string path, int index = 0)
    {
        _emojiTypeface = SKTypeface.FromFile(path, index);
        return this;
    }

    public WordCloudBuilder WithEmojiFont(SKTypeface typeface)
    {
        _emojiTypeface = typeface;
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

    public WordCloudBuilder WithSimilarity(float similiarity)
    {
        _similarity = similiarity;
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

    public WordCloudBuilder WithStrokeRatio(float strokeRatio)
    {
        _strokeRatio = strokeRatio;
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
        _backgroundImageBlur = size;
        return this;
    }

    public WordCloudBuilder WithMask(string path)
    {
        _mask = SKImage.FromEncodedData(File.ReadAllBytes(path));
        return this;
    }

    public WordCloudBuilder WithMask(SKImage image)
    {
        _mask = image;
        return this;
    }

    public WordCloudBuilder WithVerticality(float verticality)
    {
        _verticality = verticality;
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
        _emojiTypeface ?? _typeface,
        _maxFontSize,
        _minFontSize,
        _fontSizeStep,
        _similarity,
        _padding,
        _strokeWidth,
        _strokeRatio,
        _background,
        _backgroundImage,
        _backgroundImageBlur,
        _mask,
        _verticality,
        _colorFunc,
        _strokeColorFunc
    );
}
