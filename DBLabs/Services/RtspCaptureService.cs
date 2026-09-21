using System;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace DBLabs.Services
{
    /// <summary>
    /// Grabs single frames from an RTSP URL using OpenCV's FFmpeg backend.
    /// No persistent connection is kept between grabs (simple, robust for occasional captures).
    /// </summary>
    public static class RtspCaptureService
    {
        public static async Task<Mat?> GrabFrameAsync(string rtspUrl, int timeoutMs = 8000)
        {
            return await Task.Run(() =>
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                using var capture = new VideoCapture(rtspUrl, VideoCaptureAPIs.FFMPEG);
                if (!capture.IsOpened())
                    return null;

                var frame = new Mat();
                // Grab a couple of frames first; the first decoded frame after opening
                // an RTSP stream is often stale/corrupt on some cameras.
                for (int i = 0; i < 3; i++)
                {
                    if (cts.IsCancellationRequested) break;
                    capture.Read(frame);
                }

                if (frame.Empty())
                    return null;

                return frame;
            });
        }
    }
}
