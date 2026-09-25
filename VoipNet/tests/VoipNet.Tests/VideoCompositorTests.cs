using VoipNet.Video;
using Xunit;

namespace VoipNet.Tests;

/// <summary>Laying several pictures out in one, which is what a conference grid is made of.</summary>
public sealed class VideoCompositorTests
{
    [Fact]
    public void FourPicturesGoIntoFourQuarters()
    {
        var compositor = new VideoCompositor(320, 240);
        var composed = compositor.Compose(
            [Solid(160, 120, 60), Solid(160, 120, 100), Solid(160, 120, 160), Solid(160, 120, 220)],
            CompositorLayout.Grid);

        Assert.Equal(320, composed.Width);
        Assert.Equal(240, composed.Height);

        // Each quarter holds the brightness that went into it, read well inside the tile so the
        // averaging at an edge cannot be mistaken for a misplaced picture.
        Near(60, Luma(composed, 40, 30), 2);
        Near(100, Luma(composed, 280, 30), 2);
        Near(160, Luma(composed, 40, 210), 2);
        Near(220, Luma(composed, 280, 210), 2);
    }

    [Fact]
    public void ThreePicturesLeaveTheFourthCornerBlack()
    {
        var compositor = new VideoCompositor(320, 240);
        var composed = compositor.Compose([Solid(160, 120, 80), Solid(160, 120, 140), Solid(160, 120, 200)]);

        Near(80, Luma(composed, 40, 30), 2);
        Near(140, Luma(composed, 280, 30), 2);
        Near(200, Luma(composed, 40, 210), 2);
        // Nothing was put there, so it is the floor of the range rather than the last frame.
        Assert.Equal(16, Luma(composed, 280, 210));
    }

    [Fact]
    public void PictureInPicturePutsTheSecondInTheCorner()
    {
        var compositor = new VideoCompositor(320, 240);
        var composed = compositor.Compose([Solid(320, 240, 70), Solid(160, 120, 210)], CompositorLayout.PictureInPicture);

        Near(70, Luma(composed, 20, 20), 2);
        Near(70, Luma(composed, 160, 120), 2);
        Near(210, Luma(composed, 280, 200), 2);
    }

    [Fact]
    public void SpotlightKeepsTheRestAlongTheBottom()
    {
        var compositor = new VideoCompositor(320, 240);
        var composed = compositor.Compose([Solid(320, 180, 90), Solid(160, 120, 150), Solid(160, 120, 230)], CompositorLayout.Spotlight);

        Near(90, Luma(composed, 160, 60), 2);
        Near(150, Luma(composed, 80, 215), 2);
        Near(230, Luma(composed, 240, 215), 2);
    }

    [Fact]
    public void APictureKeepsItsShape()
    {
        // A tall picture in a wide tile is fitted and centred, not stretched: the sides stay black.
        var compositor = new VideoCompositor(320, 240);
        var composed = compositor.Compose([Solid(120, 240, 200)], CompositorLayout.Grid);

        Near(200, Luma(composed, 160, 120), 2);
        Assert.Equal(16, Luma(composed, 4, 120));
        Assert.Equal(16, Luma(composed, 316, 120));
    }

    [Fact]
    public void ColourSurvivesTheJourney()
    {
        // Two pictures of one colour each, side by side: the colour planes must follow the tiles.
        var compositor = new VideoCompositor(320, 240);
        var red = Solid(160, 240, 82, blue: 90, redDifference: 240);
        var blue = Solid(160, 240, 41, blue: 240, redDifference: 110);
        var composed = compositor.Compose([red, blue], CompositorLayout.Grid);

        var (leftBlue, leftRed) = Colour(composed, 80, 120);
        var (rightBlue, rightRed) = Colour(composed, 240, 120);
        Near(90, leftBlue, 3);
        Near(240, leftRed, 3);
        Near(240, rightBlue, 3);
        Near(110, rightRed, 3);
    }

    [Fact]
    public void NoPicturesMakeABlackFrame()
    {
        var compositor = new VideoCompositor(64, 64);
        var composed = compositor.Compose([]);

        Assert.Equal(VideoPicture.Nv12Length(64, 64), composed.Data.Length);
        Assert.Equal(16, Luma(composed, 32, 32));
        Assert.Equal((128, 128), Colour(composed, 32, 32));
    }

    [Fact]
    public void AnOddSizeIsRefused() => Assert.Throws<ArgumentException>(() => new VideoCompositor(321, 240));

    /// <summary>Close enough: a box filter over an edge moves a value by a little, and should.</summary>
    private static void Near(int expected, int actual, int tolerance) =>
        Assert.InRange(actual, expected - tolerance, expected + tolerance);

    private static VideoPicture Solid(int width, int height, byte luma, byte blue = 128, byte redDifference = 128)
    {
        var data = new byte[VideoPicture.Nv12Length(width, height)];
        data.AsSpan(0, width * height).Fill(luma);
        for (var i = width * height; i < data.Length; i += 2)
        {
            data[i] = blue;
            data[i + 1] = redDifference;
        }

        return new VideoPicture(width, height, data, TimeSpan.Zero);
    }

    private static int Luma(VideoPicture picture, int x, int y) => picture.Data.Span[(y * picture.Width) + x];

    private static (int Blue, int Red) Colour(VideoPicture picture, int x, int y)
    {
        var at = (picture.Width * picture.Height) + ((y / 2) * picture.Width) + (x & ~1);
        return (picture.Data.Span[at], picture.Data.Span[at + 1]);
    }
}
