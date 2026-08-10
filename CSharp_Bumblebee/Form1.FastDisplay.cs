using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using Emgu.CV;

namespace CSharp_Bumblebee
{
    public partial class Form1
    {
        private const int TargetDisplayFps = 30;
        private const int DisplayPumpPeriodMs = 1000 / TargetDisplayFps;

        private readonly object displayFrameLock = new object();
        private DisplayFrame pendingDisplayFrame;
        private DisplayFrame currentDisplayFrame;
        private System.Threading.Timer displayPumpTimer;
        private bool displayPumpRunning;
        private int displayPumpInvokePending;
        private int displayedFrameCounter;

        private void EnsureFastDisplayPump()
        {
            lock (displayFrameLock)
            {
                if (displayPumpRunning)
                    return;

                displayPumpRunning = true;
                displayPumpTimer = new System.Threading.Timer(
                    DisplayPumpTick,
                    null,
                    0,
                    DisplayPumpPeriodMs);
            }
        }

        private void QueueDisplayFrame(Mat source)
        {
            if (source == null || source.IsEmpty)
                return;

            EnsureFastDisplayPump();

            // Keep only the latest display frame. Camera capture is never allowed
            // to queue a long chain of UI work behind the current picture.
            Mat frameMat = source.Clone();
            Bitmap frameBitmap = new Bitmap(
                frameMat.Width,
                frameMat.Height,
                (int)frameMat.Step,
                PixelFormat.Format24bppRgb,
                frameMat.DataPointer);

            DisplayFrame newFrame = new DisplayFrame(frameMat, frameBitmap);
            DisplayFrame oldPending;

            lock (displayFrameLock)
            {
                oldPending = pendingDisplayFrame;
                pendingDisplayFrame = newFrame;
            }

            oldPending?.Dispose();
        }

        private void DisplayPumpTick(object state)
        {
            if (!displayPumpRunning || IsDisposed || !IsHandleCreated)
                return;

            // At most one UI callback may be waiting at any time. This prevents
            // BeginInvoke messages from flooding the WinForms message queue.
            if (Interlocked.CompareExchange(ref displayPumpInvokePending, 1, 0) != 0)
                return;

            try
            {
                BeginInvoke(new Action(ProcessPendingDisplayFrame));
            }
            catch
            {
                Interlocked.Exchange(ref displayPumpInvokePending, 0);
            }
        }

        private void ProcessPendingDisplayFrame()
        {
            try
            {
                DisplayFrame nextFrame;

                lock (displayFrameLock)
                {
                    nextFrame = pendingDisplayFrame;
                    pendingDisplayFrame = null;
                }

                if (nextFrame == null)
                    return;

                DisplayFrame oldFrame = currentDisplayFrame;
                currentDisplayFrame = nextFrame;
                pBox.Image = nextFrame.Bitmap;
                oldFrame?.Dispose();

                // Count only frames actually handed to the PictureBox.
                Interlocked.Increment(ref displayedFrameCounter);
            }
            finally
            {
                Interlocked.Exchange(ref displayPumpInvokePending, 0);
            }
        }

        private void DisposeFastDisplay()
        {
            System.Threading.Timer timer;
            DisplayFrame pending;
            DisplayFrame current;

            lock (displayFrameLock)
            {
                displayPumpRunning = false;
                timer = displayPumpTimer;
                displayPumpTimer = null;

                pending = pendingDisplayFrame;
                pendingDisplayFrame = null;

                current = currentDisplayFrame;
                currentDisplayFrame = null;
            }

            timer?.Dispose();
            Interlocked.Exchange(ref displayPumpInvokePending, 0);

            if (pBox != null)
                pBox.Image = null;

            pending?.Dispose();
            current?.Dispose();
        }
    }

    internal sealed class DisplayFrame : IDisposable
    {
        public DisplayFrame(Mat mat, Bitmap bitmap)
        {
            Mat = mat;
            Bitmap = bitmap;
        }

        public Mat Mat { get; private set; }
        public Bitmap Bitmap { get; private set; }

        public void Dispose()
        {
            Bitmap?.Dispose();
            Bitmap = null;

            Mat?.Dispose();
            Mat = null;
        }
    }
}
