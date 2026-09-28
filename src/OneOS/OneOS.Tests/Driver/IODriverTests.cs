using OneOS.Runtime.Driver;

namespace OneOS.Tests.Driver;

// I/O drivers (ported from OneOS-V5B). The streaming path runs in the live test (tools/LiveClusterTest).
public class IODriverTests
{
    [Fact]
    public void FfmpegCommandForACamera()
    {
        var args = FfmpegReader.Command(new[] { "/dev/video0", "format=v4l2" });
        Assert.Equal(new[] { "-hide_banner", "-loglevel", "error", "-f", "v4l2", "-framerate", "30", "-video_size", "640x480", "-i", "/dev/video0",
            "-an", "-b:v", "300k", "-r", "15", "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1" }, args);
    }

    [Fact]
    public void FfmpegCommandOptions()
    {
        var test = FfmpegReader.Command(new[] { "testsrc=size=320x240:rate=15", "format=lavfi", "rate=5" });
        Assert.DoesNotContain("-framerate", test);                                   // a lavfi source sets its own
        Assert.Equal("testsrc=size=320x240:rate=15", test[test.ToList().IndexOf("-i") + 1]);
        Assert.Equal("5", test[test.ToList().IndexOf("-r") + 1]);
        var windows = FfmpegReader.Command(new[] { "Integrated Webcam", "format=dshow", "size=1280x720" });
        Assert.Equal("video=Integrated Webcam", windows[windows.ToList().IndexOf("-i") + 1]);
        Assert.Equal("1280x720", windows[windows.ToList().IndexOf("-video_size") + 1]);
    }
}
