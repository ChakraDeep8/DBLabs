using System;
using OpenCvSharp;

namespace DBLabs.Services
{
    /// <summary>
    /// A stream the collector can pull frames from, one after another. Exists so the collection
    /// loop can be exercised against synthetic frames instead of requiring a live camera — the
    /// loop's timing, stop conditions and reconnect handling are the parts most worth testing,
    /// and none of them care where the pixels came from.
    /// </summary>
    public interface IFrameSource : IDisposable
    {
        /// <summary>Opens the stream. False means it could not be reached.</summary>
        bool Open();

        /// <summary>
        /// Reads the next frame into <paramref name="frame"/>. False means the read failed and the
        /// caller should treat the stream as dropped. The buffer is reused deliberately: at
        /// 25 fps, allocating a Mat per read would churn hundreds of megabytes over a long run.
        /// </summary>
        bool TryRead(Mat frame);
    }

    /// <summary>
    /// A persistent RTSP connection, held open for the whole run.
    ///
    /// Distinct from RtspCaptureService, which reconnects per grab by design and is right for
    /// occasional single captures. Reconnecting per frame here would cost seconds each time and
    /// hammer the camera thousands of times over a run, so sustained sampling needs its own path.
    /// </summary>
    public sealed class RtspFrameSource : IFrameSource
    {
        private readonly string _url;
        private VideoCapture? _capture;

        public RtspFrameSource(string url) => _url = url;

        public bool Open()
        {
            Dispose();
            try
            {
                _capture = new VideoCapture(_url, VideoCaptureAPIs.FFMPEG);
                return _capture.IsOpened();
            }
            catch
            {
                return false;
            }
        }

        public bool TryRead(Mat frame)
        {
            if (_capture == null || !_capture.IsOpened()) return false;
            try
            {
                return _capture.Read(frame) && !frame.Empty();
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            _capture?.Dispose();
            _capture = null;
        }
    }
}
