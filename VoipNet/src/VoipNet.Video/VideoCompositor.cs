namespace VoipNet.Video;

/// <summary>How several pictures are arranged into one.</summary>
public enum CompositorLayout
{
    /// <summary>Equal tiles, as square a grid as the number of pictures allows.</summary>
    Grid,

    /// <summary>The first picture fills the frame, the second sits in the bottom-right corner.</summary>
    PictureInPicture,

    /// <summary>The first picture takes the frame, the rest run along the bottom as a strip.</summary>
    Spotlight,
}

/// <summary>
/// Lays several pictures out in one, so a conference can be sent as a single stream.
/// </summary>
/// <remarks>
/// This is the mixing half of a conference, which the engine deliberately does not do: it forwards one
/// participant's video untouched, because forwarding costs nothing and keeps the quality the sender
/// chose. Composing costs a decode of every participant and one encode of the result, which is what
/// buys a grid — every face at once, one stream out, and a receiver that needs no layout of its own.
///
/// Pictures are NV12 throughout, the form both codecs and this work in. Scaling is a box filter: each
/// destination pixel is the average of the source pixels it covers, which is what keeps a shrunken
/// face from breaking up into edges. Tiles are placed on even boundaries because colour is stored for
/// each two-by-two block and half a block cannot be addressed.
/// </remarks>
public sealed class VideoCompositor
{
    /// <summary>Black, in the form NV12 stores it: the studio-range floor, with no colour at all.</summary>
    private const byte Black = 16;
    private const byte NoColour = 128;

    private readonly byte[] _canvas;

    /// <summary>Creates a compositor that produces pictures of one size.</summary>
    /// <param name="width">Output width in pixels; must be even.</param>
    /// <param name="height">Output height in pixels; must be even.</param>
    public VideoCompositor(int width, int height)
    {
        if (width <= 1 || height <= 1 || width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException("A picture with half-resolution colour has an even width and height.", nameof(width));
        }

        Width = width;
        Height = height;
        _canvas = new byte[VideoPicture.Nv12Length(width, height)];
    }

    /// <summary>Output width.</summary>
    public int Width { get; }

    /// <summary>Output height.</summary>
    public int Height { get; }

    /// <summary>
    /// Arranges the pictures given into one. The result borrows the compositor's own buffer, so it is
    /// valid until the next call — encode it or copy it before composing again.
    /// </summary>
    /// <param name="sources">The pictures, in the order the layout should honour.</param>
    /// <param name="layout">How to arrange them.</param>
    /// <param name="timestamp">The time to stamp the result with.</param>
    public VideoPicture Compose(IReadOnlyList<VideoPicture> sources, CompositorLayout layout = CompositorLayout.Grid, TimeSpan timestamp = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Clear();
        var usable = sources.Where(s => s.Width > 0 && s.Height > 0 && s.Data.Length >= VideoPicture.Nv12Length(s.Width, s.Height)).ToList();
        if (usable.Count > 0)
        {
            foreach (var (picture, tile) in Arrange(usable, layout))
            {
                Draw(picture, tile);
            }
        }

        return new VideoPicture(Width, Height, _canvas, timestamp);
    }

    /// <summary>Where each picture goes, in the order they were given.</summary>
    private IEnumerable<(VideoPicture Picture, Rectangle Tile)> Arrange(List<VideoPicture> sources, CompositorLayout layout)
    {
        switch (layout)
        {
            case CompositorLayout.PictureInPicture when sources.Count > 1:
                // The inset goes over the corner of the main picture, a quarter of the frame across.
                yield return (sources[0], new Rectangle(0, 0, Width, Height));
                var inset = new Rectangle(Even(Width - (Width / 4) - (Width / 40)), Even(Height - (Height / 4) - (Height / 40)), Even(Width / 4), Even(Height / 4));
                yield return (sources[1], inset);
                break;

            case CompositorLayout.Spotlight when sources.Count > 1:
                var stripHeight = Even(Height / 4);
                yield return (sources[0], new Rectangle(0, 0, Width, Height - stripHeight));
                var others = sources.Count - 1;
                var stripWidth = Even(Width / others);
                for (var i = 0; i < others; i++)
                {
                    yield return (sources[i + 1], new Rectangle(Even(i * stripWidth), Height - stripHeight, stripWidth, stripHeight));
                }

                break;

            default:
                // As square as the count allows: two across for two or four, three for five to nine.
                var columns = (int)Math.Ceiling(Math.Sqrt(sources.Count));
                var rows = (int)Math.Ceiling(sources.Count / (double)columns);
                var cellWidth = Even(Width / columns);
                var cellHeight = Even(Height / rows);
                for (var i = 0; i < sources.Count; i++)
                {
                    var column = i % columns;
                    var row = i / columns;
                    // The last tile in a row takes what is left, so rounding never leaves a seam.
                    var width = column == columns - 1 ? Width - (cellWidth * column) : cellWidth;
                    var height = row == rows - 1 ? Height - (cellHeight * row) : cellHeight;
                    yield return (sources[i], new Rectangle(Even(column * cellWidth), Even(row * cellHeight), Even(width), Even(height)));
                }

                break;
        }
    }

    /// <summary>Fills the canvas with black, so a gap between tiles is a gap and not last frame's picture.</summary>
    private void Clear()
    {
        var luma = Width * Height;
        _canvas.AsSpan(0, luma).Fill(Black);
        _canvas.AsSpan(luma).Fill(NoColour);
    }

    /// <summary>
    /// Draws one picture into a tile, keeping its shape: it is fitted inside the tile and centred, so
    /// a phone held upright does not come out stretched across a landscape cell.
    /// </summary>
    private void Draw(VideoPicture picture, Rectangle tile)
    {
        if (tile.Width < 2 || tile.Height < 2)
        {
            return;
        }

        var scale = Math.Min(tile.Width / (double)picture.Width, tile.Height / (double)picture.Height);
        var drawWidth = Even(Math.Max((int)Math.Round(picture.Width * scale), 2));
        var drawHeight = Even(Math.Max((int)Math.Round(picture.Height * scale), 2));
        var left = Even(tile.Left + ((tile.Width - drawWidth) / 2));
        var top = Even(tile.Top + ((tile.Height - drawHeight) / 2));
        if (left + drawWidth > Width || top + drawHeight > Height)
        {
            return;
        }

        var source = picture.Data.Span;
        var sourceLuma = picture.Width * picture.Height;
        var canvasLuma = Width * Height;

        // Brightness, at full resolution.
        for (var y = 0; y < drawHeight; y++)
        {
            var fromY = y * picture.Height / drawHeight;
            var toY = Math.Max(((y + 1) * picture.Height / drawHeight), fromY + 1);
            for (var x = 0; x < drawWidth; x++)
            {
                var fromX = x * picture.Width / drawWidth;
                var toX = Math.Max(((x + 1) * picture.Width / drawWidth), fromX + 1);
                var total = 0;
                var count = 0;
                for (var sy = fromY; sy < toY; sy++)
                {
                    for (var sx = fromX; sx < toX; sx++)
                    {
                        total += source[(sy * picture.Width) + sx];
                        count++;
                    }
                }

                _canvas[((top + y) * Width) + left + x] = (byte)(total / count);
            }
        }

        // Colour, at half resolution in both directions, one pair of values per two-by-two block.
        for (var y = 0; y < drawHeight / 2; y++)
        {
            var fromY = y * (picture.Height / 2) / (drawHeight / 2);
            var toY = Math.Max((y + 1) * (picture.Height / 2) / (drawHeight / 2), fromY + 1);
            for (var x = 0; x < drawWidth / 2; x++)
            {
                var fromX = x * (picture.Width / 2) / (drawWidth / 2);
                var toX = Math.Max((x + 1) * (picture.Width / 2) / (drawWidth / 2), fromX + 1);
                var blue = 0;
                var red = 0;
                var count = 0;
                for (var sy = fromY; sy < toY; sy++)
                {
                    for (var sx = fromX; sx < toX; sx++)
                    {
                        var at = sourceLuma + (sy * picture.Width) + (sx * 2);
                        blue += source[at];
                        red += source[at + 1];
                        count++;
                    }
                }

                var into = canvasLuma + (((top / 2) + y) * Width) + left + (x * 2);
                _canvas[into] = (byte)(blue / count);
                _canvas[into + 1] = (byte)(red / count);
            }
        }
    }

    private static int Even(int value) => value & ~1;

    private readonly record struct Rectangle(int Left, int Top, int Width, int Height);
}
