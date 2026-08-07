using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace CSharp_Bumblebee
{
    public partial class Form1
    {
        private readonly Stopwatch performanceClock = new Stopwatch();
        private readonly Stopwatch uiPaintClock = new Stopwatch();
        private readonly Process currentProcess = Process.GetCurrentProcess();
        private Timer performanceTimer;
        private int lastPerformanceFrameCount;
        private double displayFps;
        private double poseFps;
        private double frameMs;
        private double uiPaintMs;
        private double processCpuPercent;
        private TimeSpan lastProcessCpuTime;
        private DateTime lastCpuSampleTime;

        // Measured pipeline timings. These are updated from the capture worker.
        private readonly object profilingLock = new object();
        private double captureMs;
        private double copyMs;
        private double convertMs;
        private double inferenceMs;
        private double posePostMs;
        private double distanceRenderMs;
        private double bitmapQueueMs;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            InitializePerformanceOverlay();
        }

        private void InitializePerformanceOverlay()
        {
            if (performanceTimer != null)
                return;

            performanceClock.Restart();
            lastPerformanceFrameCount = poseFrameCounter;
            lastProcessCpuTime = currentProcess.TotalProcessorTime;
            lastCpuSampleTime = DateTime.UtcNow;
            pBox.Paint += pBox_PerformancePaint;

            performanceTimer = new Timer();
            performanceTimer.Interval = 1000;
            performanceTimer.Tick += performanceTimer_Tick;
            performanceTimer.Start();
        }

        private static double SmoothTiming(double oldValue, double newValue)
        {
            if (newValue < 0) return oldValue;
            return oldValue <= 0 ? newValue : oldValue * 0.8 + newValue * 0.2;
        }

        private void SetCaptureMs(double value) { lock (profilingLock) captureMs = SmoothTiming(captureMs, value); }
        private void SetCopyMs(double value) { lock (profilingLock) copyMs = SmoothTiming(copyMs, value); }
        private void SetConvertMs(double value) { lock (profilingLock) convertMs = SmoothTiming(convertMs, value); }
        private void SetInferenceMs(double value) { lock (profilingLock) inferenceMs = SmoothTiming(inferenceMs, value); }
        private void SetPosePostMs(double value) { lock (profilingLock) posePostMs = SmoothTiming(posePostMs, value); }
        private void SetDistanceRenderMs(double value) { lock (profilingLock) distanceRenderMs = SmoothTiming(distanceRenderMs, value); }
        private void SetBitmapQueueMs(double value) { lock (profilingLock) bitmapQueueMs = SmoothTiming(bitmapQueueMs, value); }

        private void performanceTimer_Tick(object sender, EventArgs e)
        {
            double seconds = performanceClock.Elapsed.TotalSeconds;
            if (seconds <= 0) return;

            int currentFrameCount = poseFrameCounter;
            int frameDelta = currentFrameCount - lastPerformanceFrameCount;
            if (frameDelta < 0) frameDelta = currentFrameCount;

            displayFps = frameDelta / seconds;
            poseFps = cachedPosePeople.Count > 0 ? displayFps / PoseInferenceInterval : displayFps;
            frameMs = displayFps > 0 ? 1000.0 / displayFps : 0;

            DateTime now = DateTime.UtcNow;
            TimeSpan cpuNow = currentProcess.TotalProcessorTime;
            double wallMs = (now - lastCpuSampleTime).TotalMilliseconds;
            double cpuMs = (cpuNow - lastProcessCpuTime).TotalMilliseconds;
            if (wallMs > 0 && Environment.ProcessorCount > 0)
            {
                processCpuPercent = cpuMs / (wallMs * Environment.ProcessorCount) * 100.0;
                if (processCpuPercent < 0) processCpuPercent = 0;
                if (processCpuPercent > 100) processCpuPercent = 100;
            }

            lastProcessCpuTime = cpuNow;
            lastCpuSampleTime = now;
            lastPerformanceFrameCount = currentFrameCount;
            performanceClock.Restart();
            pBox.Invalidate();
        }

        private void pBox_PerformancePaint(object sender, PaintEventArgs e)
        {
            uiPaintClock.Restart();

            double c, cp, cv, inf, post, dist, bmp;
            lock (profilingLock)
            {
                c = captureMs; cp = copyMs; cv = convertMs; inf = inferenceMs;
                post = posePostMs; dist = distanceRenderMs; bmp = bitmapQueueMs;
            }

            string info =
                "Display FPS : " + displayFps.ToString("F1") + Environment.NewLine +
                "Frame       : " + frameMs.ToString("F1") + " ms" + Environment.NewLine +
                "Capture     : " + c.ToString("F1") + " ms" + Environment.NewLine +
                "Copy        : " + cp.ToString("F1") + " ms" + Environment.NewLine +
                "Convert     : " + cv.ToString("F1") + " ms" + Environment.NewLine +
                "Inference   : " + inf.ToString("F1") + " ms" + Environment.NewLine +
                "Pose Post   : " + post.ToString("F1") + " ms" + Environment.NewLine +
                "Dist/Draw   : " + dist.ToString("F1") + " ms" + Environment.NewLine +
                "BitmapQueue : " + bmp.ToString("F1") + " ms" + Environment.NewLine +
                "UI Paint    : " + uiPaintMs.ToString("F2") + " ms" + Environment.NewLine +
                "Process CPU : " + processCpuPercent.ToString("F1") + "%" + Environment.NewLine +
                "Pose FPS    : " + poseFps.ToString("F1") + Environment.NewLine +
                "Pose Int.   : " + PoseInferenceInterval + Environment.NewLine +
                "People      : " + cachedPosePeople.Count;

            using (Font font = new Font("Consolas", 9.0f, FontStyle.Bold))
            {
                SizeF textSize = e.Graphics.MeasureString(info, font);
                const int padding = 7;
                int x = Math.Max(4, pBox.ClientSize.Width - (int)Math.Ceiling(textSize.Width) - padding * 2 - 6);
                int y = Math.Max(4, pBox.ClientSize.Height - (int)Math.Ceiling(textSize.Height) - padding * 2 - 6);
                Rectangle background = new Rectangle(x, y, (int)Math.Ceiling(textSize.Width) + padding * 2, (int)Math.Ceiling(textSize.Height) + padding * 2);

                using (SolidBrush backgroundBrush = new SolidBrush(Color.FromArgb(165, 0, 0, 0)))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    e.Graphics.FillRectangle(backgroundBrush, background);
                    e.Graphics.DrawString(info, font, textBrush, x + padding, y + padding);
                }
            }

            uiPaintClock.Stop();
            uiPaintMs = SmoothTiming(uiPaintMs, uiPaintClock.Elapsed.TotalMilliseconds);
        }

        private void DisposePerformanceOverlay()
        {
            if (performanceTimer == null) return;
            performanceTimer.Stop();
            performanceTimer.Tick -= performanceTimer_Tick;
            performanceTimer.Dispose();
            performanceTimer = null;
            performanceClock.Stop();
            uiPaintClock.Stop();
            currentProcess.Dispose();
        }
    }
}
