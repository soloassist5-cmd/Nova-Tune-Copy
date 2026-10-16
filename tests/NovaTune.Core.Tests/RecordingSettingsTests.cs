using NovaTune.Core.Settings;
using Xunit;

namespace NovaTune.Core.Tests;

public class RecordingSettingsTests
{
    [Fact]
    public void Normalize_FixesInvalidRecordingValues()
    {
        var s = new AppSettings { Recording = null! };
        s.Normalize();
        Assert.NotNull(s.Recording);

        s.Recording.Fps = 144;
        s.Recording.Quality = 9;
        s.Recording.OverlayCorner = -3;
        s.Normalize();
        Assert.Equal(60, s.Recording.Fps);
        Assert.Equal(2, s.Recording.Quality);
        Assert.Equal(0, s.Recording.OverlayCorner);
    }

    [Fact]
    public void Normalize_KeepsValidValues()
    {
        var s = new AppSettings();
        s.Recording.Fps = 30;
        s.Recording.Quality = 0;
        s.Recording.OverlayCorner = 3;
        s.Normalize();
        Assert.Equal(30, s.Recording.Fps);
        Assert.Equal(0, s.Recording.Quality);
        Assert.Equal(3, s.Recording.OverlayCorner);
    }
}
