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

    public HarfBuzzMeasuring(SKTypeface skface)
    {
        blob = skface.OpenStream().ToHarfBuzzBlob();
        face = new Face(blob, 0) { UnitsPerEm = skface.UnitsPerEm };
        font = new Font(face);
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

        var width = buffer.GlyphPositions.Sum(pos => pos.XAdvance) * fontSize / face.UnitsPerEm;

        // this is not needed, since scale in harfbuzz defaults to UPEM
        // var height = face.UnitsPerEm * fontSize / yScale;

        return (width, fontSize);
    }
}

public partial class WordCloud(
    int width,
    int height,
    float scale, // calculate position on low resolution and scale up
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
    private readonly int _lowResWidth = (int)(width / scale);
    private readonly int _lowResHeight = (int)(height / scale);
    private readonly SKSurface _lowResSurface = SKSurface.Create(
        new SKImageInfo(
            (int)(width / scale),
            (int)(height / scale),
            SKColorType.Rgba8888
        )
    );
    private readonly SKSurface _surface = SKSurface.Create(
        new SKImageInfo(
            width,
            height,
            SKColorType.Rgba8888
        )
    );
    private readonly Func<string, SKColor> _colorFunc = colorFunc ?? (s => SKColor.Parse("aaffffff"));
    private readonly Func<string, SKColor> _strokeColorFunc = strokeColorFunc ?? (s => SKColors.Black);
    private readonly int[] _matrix = new int[(int)(width / scale) * (int)(height / scale)];
    private readonly SKShaper _shaper = new SKShaper(typeface ?? SKTypeface.Default);
    private readonly SKShaper _emojiShaper = new SKShaper(emojiTypeface ?? SKTypeface.Default);
    private readonly SKPaint _paint = new SKPaint { IsAntialias = true };
    private readonly HarfBuzzMeasuring _measuring = new HarfBuzzMeasuring(typeface ?? SKTypeface.Default);
    private readonly HarfBuzzMeasuring _emojiMeasuring = new HarfBuzzMeasuring(emojiTypeface ?? SKTypeface.Default);
    private readonly bool _isScaled = scale is not 1.0f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetIndex(int x, int y) => y * _lowResWidth + x;

    [LibraryImport("wordcloud", EntryPoint = "cumulative_sum")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSuppressGCTransition)])]
    private static partial void CumulativeSum([In, Out] int[] arr, int width, int height);

    [LibraryImport("wordcloud", EntryPoint = "hit_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSuppressGCTransition)])]
    private static partial void HitCount([In, Out] int[] arr, int width, int height, int bw, int bh, [Out] int[] hits);

    private SKPoint? GetTextPosition(
        SKPixmap pixmap,
        float blockWidth,
        float blockHeight,
        bool vertical
    )
    {
        var (bw, bh) = ((int)Math.Ceiling(blockWidth + 2 * padding), (int)Math.Ceiling(blockHeight * 1.2f + 2 * padding));
        if (vertical) (bw, bh) = (bh, bw);

        if (bw >= pixmap.Width || bh >= pixmap.Height) return null;

        pixmap.GetPixelSpan<int>().CopyTo(_matrix);
        CumulativeSum(_matrix, pixmap.Width, pixmap.Height);

        var hits = ArrayPool<int>.Shared.Rent(pixmap.Height - bh);
        HitCount(_matrix, pixmap.Width, pixmap.Height, bw, bh, hits);
        var hitSpan = hits.AsSpan(0, pixmap.Height - bh);

        if (hitSpan[^1] == 0) return null;

        int index = Random.Shared.Next(hitSpan[^1]) + 1;
        int row = hitSpan.BinarySearch(index);
        while (row > 0 && hitSpan[row - 1] == hitSpan[row]) row--;
        if (row < 0) row = ~row;
        index -= row == 0 ? 0 : hitSpan[row - 1];

        int count = 0;

        for (int x = 0; x < pixmap.Width - bw; x++)
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

    private void FillText(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        SKColor color,
        SKShaper shaper
    )
    {
        _paint.Style = SKPaintStyle.Fill;
        _paint.Color = color;
        canvas.DrawShapedText(shaper, text, x, y, _paint);
    }

    private void StrokeText(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        SKColor color,
        SKShaper shaper
    )
    {
        _paint.Style = SKPaintStyle.Stroke;
        _paint.Color = color;

        if (strokeRatio > 0)
            _paint.StrokeWidth = _paint.TextSize * strokeRatio;
        else
            _paint.StrokeWidth = strokeWidth;
        canvas.DrawShapedText(shaper, text, x, y, _paint);
    }

    private void FillAndStrokeText(
        SKCanvas canvas,
        string text,
        bool isEmoji,
        float x,
        float y,
        SKColor color,
        SKColor strokeColor,
        SKShaper shaper
    )
    {
        FillText(canvas, text, x, y, color, shaper);
        if ((strokeWidth > 0 || strokeRatio > 0.0f) && !isEmoji)
            StrokeText(canvas, text, x, y, strokeColor, shaper);
    }

    private void DrawText(
        SKCanvas canvas,
        string text,
        float fontSize,
        bool isEmoji,
        bool vertical,
        float x,
        float y,
        SKColor color,
        SKColor strokeColor,
        SKShaper shaper
    )
    {
        canvas.Save();

        _paint.TextSize = fontSize;
        if (vertical)
        {
            canvas.RotateDegrees(90);
            FillAndStrokeText(canvas, text, isEmoji, y, -x, color, strokeColor, shaper);
        }
        else FillAndStrokeText(canvas, text, isEmoji, x, y, color, strokeColor, shaper);

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

        var lowResCanvas = _lowResSurface.Canvas;
        lowResCanvas.Clear();

        List<(float x, float y, float fontSize)>? positions = null;
        if (_isScaled)
        {
            positions = new List<(float, float, float)>(list.Count);
        }

        DrawMask(lowResCanvas);

        float fontSize = maxFontSize / scale;

        for (int i = 0; i < list.Count && fontSize >= minFontSize / scale; i++)
        {
            using var pixmap = _lowResSurface.PeekPixels();
            var (text, freq, vertical, isEmoji) = list[i];

            var (w, h) = (isEmoji ? _emojiMeasuring : _measuring).MeasureText(text, fontSize);

            if (GetTextPosition(pixmap, w, h, vertical) is not { } position)
            {
                i--;
                fontSize -= fontSizeStep / scale;
                continue;
            }

            positions?.Add((position.X * scale, position.Y * scale, fontSize * scale));
            DrawText(
                lowResCanvas,
                text,
                fontSize,
                isEmoji,
                vertical,
                position.X,
                position.Y,
                _isScaled ? SKColors.Black : _colorFunc(text),
                _isScaled ? SKColors.Black : _strokeColorFunc(text),
                isEmoji ? _emojiShaper : _shaper
            );
            if (i < list.Count - 1)
            {
                fontSize *= 1 - (1 - (float)list[i + 1].Freq / (float)freq) / similarity;
            }
        }

        ClearMask(lowResCanvas);

        if (!_isScaled)
        {
            DrawBackground(lowResCanvas);
            return _lowResSurface.Snapshot();
        }

        var canvas = _surface.Canvas;
        canvas.Clear();
        DrawBackground(canvas);
        for (int i = 0; i < positions!.Count; i++)
        {
            (var x, var y, fontSize) = positions[i];
            var (text, _, vertical, isEmoji) = list[i];
            DrawText(canvas, text, fontSize, isEmoji, vertical, x, y, _colorFunc(text), _strokeColorFunc(text), isEmoji ? _emojiShaper : _shaper);
        }
        return _surface.Snapshot();
    }

    public void Dispose()
    {
        _lowResSurface.Dispose();
        _surface.Dispose();
        _measuring.Dispose();
        _emojiMeasuring.Dispose();
        _shaper.Dispose();
        _emojiShaper.Dispose();
        _paint.Dispose();
    }
}

public class WordCloudBuilder
{
    private int _width = 800;
    private int _height = 600;
    private float _scale = 1.0f;
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

    public WordCloudBuilder WithScale(float scale)
    {
        _scale = scale;
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
        _scale,
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
