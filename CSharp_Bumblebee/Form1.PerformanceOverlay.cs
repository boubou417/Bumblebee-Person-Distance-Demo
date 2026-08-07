using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace CSharp_Bumblebee
{
    public partial class Form1
    {
        private readonly Stopwatch performanceClock = new Stopwatch();
        private Timer performanceTimer;
        private int lastPerformanceFrameCount;
        private double displayFps;
        private double poseFps;

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

            pBox.Paint += pBox_PerformancePaint;

            performanceTimer = new Timer();
            performanceTimer.Interval = 1000;
            performanceTimer.Tick += performanceTimer_Tick;
            performanceTimer.Start();
        }

        private void performanceTimer_Tick(object sender, EventArgs e)
        {
            double seconds = performanceClock.Elapsed.TotalSeconds;
            if (seconds <= 0)
                return;

            int currentFrameCount = poseFrameCounter;
            int frameDelta = currentFrameCount - lastPerformanceFrameCount;
            if (frameDelta < 0)
                frameDelta = currentFrameCount;

            displayFps = frameDelta / seconds;

            // With a valid cached pose, inference runs once every PoseInferenceInterval frames.
            // When nobody is detected the current V1 logic retries inference every frame,
            // so show the actual expected inference cadence for that state.
            poseFps = cachedPosePeople.Count > 0
                ? displayFps / PoseInferenceInterval
                : displayFps;

            lastPerformanceFrameCount = currentFrameCount;
            performanceClock.Restart();
            pBox.Invalidate();
        }

        private void pBox_PerformancePaint(object sender, PaintEventArgs e)
        {
            string info =
                "Display FPS : " + displayFps.ToString("F1") + Environment.NewLine +
                "Pose FPS    : " + poseFps.ToString("F1") + Environment.NewLine +
                "Pose Int.   : " + PoseInferenceInterval + Environment.NewLine +
                "People      : " + cachedPosePeople.Count;

            using (Font font = new Font("Consolas", 10.0f, FontStyle.Bold))
            {
                SizeF textSize = e.Graphics.MeasureString(info, font);
                const int padding = 8;
                int x = Math.Max(4, pBox.ClientSize.Width - (int)Math.Ceiling(textSize.Width) - padding * 2 - 6);
                int y = Math.Max(4, pBox.ClientSize.Height - (int)Math.Ceiling(textSize.Height) - padding * 2 - 6);

                Rectangle background = new Rectangle(
                    x,
                    y,
                    (int)Math.Ceiling(textSize.Width) + padding * 2,
                    (int)Math.Ceiling(textSize.Height) + padding * 2);

                using (SolidBrush backgroundBrush = new SolidBrush(Color.FromArgb(155, 0, 0, 0)))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    e.Graphics.FillRectangle(backgroundBrush, background);
                    e.Graphics.DrawString(info, font, textBrush, x + padding, y + padding);
                }
            }
        }

        private void DisposePerformanceOverlay()
        {
            if (performanceTimer == null)
                return;

            performanceTimer.Stop();
            performanceTimer.Tick -= performanceTimer_Tick;
            performanceTimer.Dispose();
            performanceTimer = null;
            performanceClock.Stop();
        }
    }
}
