using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using Emgu.CV;

namespace CSharp_Bumblebee
{
    public partial class Form1
    {
        private readonly object displayFrameLock = new object();
        private DisplayFrame pendingDisplayFrame;
        private DisplayFrame currentDisplayFrame;
        private bool displayInvokePending;
        private int displayedFrameCounter;

        private void QueueDisplayFrame(Mat source)
        {
            if (source == null || source.IsEmpty)
                return;

            Mat frameMat = source.Clone();
            Bitmap frameBitmap = new Bitmap(
                frameMat.Width,
                frameMat.Height,
                (int)frameMat.Step,
                PixelFormat.Format24bppRgb,
                frameMat.DataPointer);

            DisplayFrame newFrame = new DisplayFrame(frameMat, frameBitmap);
            DisplayFrame oldPending = null;
            bool shouldPost = false;

            lock (displayFrameLock)
            {
                oldPending = pendingDisplayFrame;
                pendingDisplayFrame = newFrame;

                if (!displayInvokePending)
                {
                    displayInvokePending = true;
                    shouldPost = true;
                }
            }

            oldPending?.Dispose();

            if (shouldPost && !IsDisposed && IsHandleCreated)
            {
                try
                {
                    BeginInvoke(new Action(ProcessPendingDisplayFrame));
                }
                catch
                {
                    lock (displayFrameLock)
                        displayInvokePending = false;
                }
            }
        }

        private void ProcessPendingDisplayFrame()
        {
            DisplayFrame nextFrame;

            lock (displayFrameLock)
            {
                nextFrame = pendingDisplayFrame;
                pendingDisplayFrame = null;
                displayInvokePending = false;
            }

            if (nextFrame != null)
            {
                DisplayFrame oldFrame = currentDisplayFrame;
                currentDisplayFrame = nextFrame;
                pBox.Image = nextFrame.Bitmap;
                oldFrame?.Dispose();

                // Count frames that actually reached the PictureBox, not merely
                // frames produced by the camera worker.
                Interlocked.Increment(ref displayedFrameCounter);
            }

            bool repost = false;
            lock (displayFrameLock)
            {
                if (pendingDisplayFrame != null && !displayInvokePending)
                {
                    displayInvokePending = true;
                    repost = true;
                }
            }

            if (repost && !IsDisposed && IsHandleCreated)
            {
                try
                {
                    BeginInvoke(new Action(ProcessPendingDisplayFrame));
                }
                catch
                {
                    lock (displayFrameLock)
                        displayInvokePending = false;
                }
            }
        }

        private void DisposeFastDisplay()
        {
            DisplayFrame pending;
            DisplayFrame current;

            lock (displayFrameLock)
            {
                pending = pendingDisplayFrame;
                pendingDisplayFrame = null;
                current = currentDisplayFrame;
                currentDisplayFrame = null;
                displayInvokePending = false;
            }

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
