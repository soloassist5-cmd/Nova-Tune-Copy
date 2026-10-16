using System.Text;
using NovaTune.Core.Logging;
using NovaTune.Platform.Recording;

namespace NovaTune.App.Services;

/// <summary>Режим --record-test: записывает 3 секунды основного монитора и сообщает результат (для CI и диагностики).</summary>
public static class RecordTest
{
    public static int Run(string outputPath, ILog log)
    {
        var sb = new StringBuilder();
        var code = 0;
        try
        {
            var monitors = ScreenRecorder.GetMonitors();
            sb.AppendLine("Monitors: " + string.Join("; ", monitors.Select(m => m.Title)));
            var monitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
            if (monitor is null)
            {
                sb.AppendLine("No monitors");
                code = 2;
            }
            else
            {
                var file = Path.Combine(Path.GetTempPath(), "novatune-record-test.mp4");
                using var recorder = new ScreenRecorder(log);
                string? failure = null;
                recorder.Failed += (_, m) => failure = m;
                recorder.Start(new RecordingOptions(monitor, 30, 0.1, true, file));
                Thread.Sleep(3000);
                recorder.Stop();
                var size = File.Exists(file) ? new FileInfo(file).Length : 0;
                sb.AppendLine($"File: {file}, {size} bytes, frames {recorder.Frames}, dropped {recorder.DroppedFrames}, audio {recorder.HasAudio}, failure: {failure ?? "none"}");
                code = size > 0 && failure is null ? 0 : 3;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            sb.AppendLine("ERROR: " + ex);
            code = 1;
        }

        File.WriteAllText(outputPath, sb.ToString());
        return code;
    }
}
