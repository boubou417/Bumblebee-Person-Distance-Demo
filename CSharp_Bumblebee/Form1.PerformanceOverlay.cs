using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace CSharp_Bumblebee
{
    public partial class Form1
    {
        private readonly Stopwatch performanceClock = new Stopwatch();
        private readonly Stopwatch uiPaintClock = new Stopwatch();
        private readonly Process currentProcess = Process.GetCurrentProcess();
        private System.Threading.Timer performanceTimer;

        private int lastCameraFrameCount;
        private int lastDisplayedFrameCount;
        private int lastDetectionInferenceCount;

        private double cameraFps;
        private double displayFps;
        private double detectionFps;
        private double frameMs;
        private double uiPaintMs;
        private double processCpuPercent;
        private TimeSpan lastProcessCpuTime;
        private DateTime lastCpuSampleTime;

        private readonly object profilingLock = new object();
        private double captureMs;
        private double copyMs;
        private double convertMs;
        private double detectionPrepMs;
        private double tensorPrepMs;
        private double inferenceMs;
        private double detectionPostMs;
        private double distanceRenderMs;
        private double bitmapQueueMs;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            InitializeUiPolish();
            InitializePerformanceOverlay();
        }

        private void InitializePerformanceOverlay()
        {
            if (performanceTimer != null)
                return;

            lastCameraFrameCount = Volatile.Read(ref cameraFrameCounter);
            lastDisplayedFrameCount = Volatile.Read(ref displayedFrameCounter);
            lastDetectionInferenceCount =
                Volatile.Read(ref detectionInferenceCounter);

            performanceClock.Restart();
            lastProcessCpuTime = currentProcess.TotalProcessorTime;
            lastCpuSampleTime = DateTime.UtcNow;
            pBox.Paint += pBox_PerformancePaint;

            performanceTimer = new System.Threading.Timer(
                performanceTimer_Tick,
                null,
                1000,
                1000);
        }

        private static double SmoothTiming(double oldValue, double newValue)
        {
            if (newValue < 0)
                return oldValue;

            return oldValue <= 0
                ? newValue
                : oldValue * 0.8 + newValue * 0.2;
        }

        private void SetCaptureMs(double value)
        {
            lock (profilingLock)
                captureMs = SmoothTiming(captureMs, value);
        }

        private void SetCopyMs(double value)
        {
            lock (profilingLock)
                copyMs = SmoothTiming(copyMs, value);
        }

        private void SetConvertMs(double value)
        {
            lock (profilingLock)
                convertMs = SmoothTiming(convertMs, value);
        }

        private void SetDetectionPrepMs(double value)
        {
            lock (profilingLock)
                detectionPrepMs = SmoothTiming(detectionPrepMs, value);
        }

        private void SetTensorPrepMs(double value)
        {
            lock (profilingLock)
                tensorPrepMs = SmoothTiming(tensorPrepMs, value);
        }

        private void SetInferenceMs(double value)
        {
            lock (profilingLock)
                inferenceMs = SmoothTiming(inferenceMs, value);
        }

        private void SetDetectionPostMs(double value)
        {
            lock (profilingLock)
                detectionPostMs = SmoothTiming(detectionPostMs, value);
        }

        private void SetDistanceRenderMs(double value)
        {
            lock (profilingLock)
                distanceRenderMs = SmoothTiming(distanceRenderMs, value);
        }

        private void SetBitmapQueueMs(double value)
        {
            lock (profilingLock)
                bitmapQueueMs = SmoothTiming(bitmapQueueMs, value);
        }

        private void performanceTimer_Tick(object state)
        {
            double seconds = performanceClock.Elapsed.TotalSeconds;
            if (seconds <= 0)
                return;

            int currentCameraCount = Volatile.Read(ref cameraFrameCounter);
            int currentDisplayedCount = Volatile.Read(ref displayedFrameCounter);
            int currentDetectionCount =
                Volatile.Read(ref detectionInferenceCounter);

            int cameraDelta = GetCounterDelta(
                currentCameraCount,
                lastCameraFrameCount);
            int displayDelta = GetCounterDelta(
                currentDisplayedCount,
                lastDisplayedFrameCount);
            int detectionDelta = GetCounterDelta(
                currentDetectionCount,
                lastDetectionInferenceCount);

            cameraFps = cameraDelta / seconds;
            displayFps = displayDelta / seconds;
            detectionFps = detectionDelta / seconds;
            frameMs = displayFps > 0 ? 1000.0 / displayFps : 0;

            DateTime now = DateTime.UtcNow;
            TimeSpan cpuNow = currentProcess.TotalProcessorTime;
            double wallMs = (now - lastCpuSampleTime).TotalMilliseconds;
            double cpuMs = (cpuNow - lastProcessCpuTime).TotalMilliseconds;

            if (wallMs > 0 && Environment.ProcessorCount > 0)
            {
                processCpuPercent =
                    cpuMs / (wallMs * Environment.ProcessorCount) * 100.0;

                if (processCpuPercent < 0)
                    processCpuPercent = 0;
                if (processCpuPercent > 100)
                    processCpuPercent = 100;
            }

            lastProcessCpuTime = cpuNow;
            lastCpuSampleTime = now;
            lastCameraFrameCount = currentCameraCount;
            lastDisplayedFrameCount = currentDisplayedCount;
            lastDetectionInferenceCount = currentDetectionCount;
            performanceClock.Restart();

            if (!IsDisposed && IsHandleCreated)
            {
                try
                {
                    BeginInvoke(new Action(() => pBox.Invalidate()));
                }
                catch
                {
                }
            }
        }

        private static int GetCounterDelta(int current, int previous)
        {
            if (current >= previous)
                return current - previous;

            return current;
        }

        private void pBox_PerformancePaint(object sender, PaintEventArgs e)
        {
            if (!showDebugOverlay)
                return;

            uiPaintClock.Restart();

            double capture;
            double copy;
            double convert;
            double prep;
            double tensor;
            double inference;
            double post;
            double distance;
            double bitmap;

            lock (profilingLock)
            {
                capture = captureMs;
                copy = copyMs;
                convert = convertMs;
                prep = detectionPrepMs;
                tensor = tensorPrepMs;
                inference = inferenceMs;
                post = detectionPostMs;
                distance = distanceRenderMs;
                bitmap = bitmapQueueMs;
            }

            string state = capImg ? "RUN" : "STOP";
            string info =
                "Mode        : LIGHT DETECTION" + Environment.NewLine +
                "State       : " + state + Environment.NewLine +
                "Camera FPS  : " + cameraFps.ToString("F1") + Environment.NewLine +
                "Display FPS : " + displayFps.ToString("F1") + Environment.NewLine +
                "Frame       : " + frameMs.ToString("F1") + " ms" + Environment.NewLine +
                "Capture     : " + capture.ToString("F1") + " ms" + Environment.NewLine +
                "Copy        : " + copy.ToString("F1") + " ms" + Environment.NewLine +
                "Convert     : " + convert.ToString("F1") + " ms" + Environment.NewLine +
                "Detect Prep : " + prep.ToString("F1") + " ms" + Environment.NewLine +
                "Tensor Prep : " + tensor.ToString("F1") + " ms" + Environment.NewLine +
                "Inference   : " + inference.ToString("F1") + " ms" + Environment.NewLine +
                "Detect Post : " + post.ToString("F1") + " ms" + Environment.NewLine +
                "Dist/Draw   : " + distance.ToString("F1") + " ms" + Environment.NewLine +
                "BitmapQueue : " + bitmap.ToString("F1") + " ms" + Environment.NewLine +
                "UI Paint    : " + uiPaintMs.ToString("F2") + " ms" + Environment.NewLine +
                "Process CPU : " + processCpuPercent.ToString("F1") + "%" + Environment.NewLine +
                "Detect FPS  : " + detectionFps.ToString("F1") + Environment.NewLine +
                "Detect EP   : " + DetectionBackendName + Environment.NewLine +
                "Model       : " + GetDetectionModelLabel() + Environment.NewLine +
                "Input Size  : " + GetDetectionInputSize() + Environment.NewLine +
                "Detect Src  : " + DetectionSourceInterval + Environment.NewLine +
                "Det Raw     : " + GetRawDetectionPeopleCount() + Environment.NewLine +
                "Det Kept    : " + GetKeptDetectionPeopleCount() + Environment.NewLine +
                "People      : " + GetDetectionPeopleCount() + Environment.NewLine +
                GetCaptureDiagnosticsOverlayText();

            using (Font font = new Font("Consolas", 9.0f, FontStyle.Bold))
            {
                SizeF textSize = e.Graphics.MeasureString(info, font);
                const int padding = 7;
                int x = Math.Max(
                    4,
                    pBox.ClientSize.Width -
                    (int)Math.Ceiling(textSize.Width) -
                    padding * 2 - 6);
                int y = Math.Max(
                    4,
                    pBox.ClientSize.Height -
                    (int)Math.Ceiling(textSize.Height) -
                    padding * 2 - 6);
                Rectangle background = new Rectangle(
                    x,
                    y,
                    (int)Math.Ceiling(textSize.Width) + padding * 2,
                    (int)Math.Ceiling(textSize.Height) + padding * 2);

                using (SolidBrush backgroundBrush =
                    new SolidBrush(Color.FromArgb(165, 0, 0, 0)))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    e.Graphics.FillRectangle(backgroundBrush, background);
                    e.Graphics.DrawString(
                        info,
                        font,
                        textBrush,
                        x + padding,
                        y + padding);
                }
            }

            uiPaintClock.Stop();
            uiPaintMs = SmoothTiming(
                uiPaintMs,
                uiPaintClock.Elapsed.TotalMilliseconds);
        }

        private void DisposePerformanceOverlay()
        {
            System.Threading.Timer timer = performanceTimer;
            performanceTimer = null;

            if (timer != null)
            {
                try
                {
                    timer.Change(Timeout.Infinite, Timeout.Infinite);
                }
                catch
                {
                }

                timer.Dispose();
            }

            performanceClock.Stop();
            uiPaintClock.Stop();
            currentProcess.Dispose();
        }
    }
}
