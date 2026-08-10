using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using Emgu.CV;
using Emgu.CV.CvEnum;

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
        private int displayTargetWidth;
        private int displayTargetHeight;
        private bool displaySizingInitialized;

        private void InitializeFastDisplaySizing()
        {
            if (displaySizingInitialized || pBox == null)
                return;

            displaySizingInitialized = true;
            pBox.SizeMode = System.Windows.Forms.PictureBoxSizeMode.CenterImage;
            pBox.SizeChanged += pBox_FastDisplaySizeChanged;
            UpdateFastDisplayTargetSize();
        }

        private void pBox_FastDisplaySizeChanged(object sender, EventArgs e)
        {
            UpdateFastDisplayTargetSize();
        }

        private void UpdateFastDisplayTargetSize()
        {
            if (pBox == null)
                return;

            Volatile.Write(ref displayTargetWidth, Math.Max(1, pBox.ClientSize.Width));
            Volatile.Write(ref displayTargetHeight, Math.Max(1, pBox.ClientSize.Height));
        }

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

            int targetWidth = Volatile.Read(ref displayTargetWidth);
            int targetHeight = Volatile.Read(ref displayTargetHeight);

            if (targetWidth <= 0 || targetHeight <= 0)
            {
                targetWidth = source.Width;
                targetHeight = source.Height;
            }

            double scale = Math.Min(
                targetWidth / (double)source.Width,
                targetHeight / (double)source.Height);

            int outputWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
            int outputHeight = Math.Max(1, (int)Math.Round(source.Height * scale));

            Mat frameMat = new Mat();

            if (outputWidth == source.Width && outputHeight == source.Height)
            {
                source.CopyTo(frameMat);
            }
            else
            {
                Inter interpolation = scale < 1.0
                    ? Inter.Area
                    : Inter.Linear;

                CvInvoke.Resize(
                    source,
                    frameMat,
                    new Size(outputWidth, outputHeight),
                    0,
                    0,
                    interpolation);
            }

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

            if (displaySizingInitialized && pBox != null)
            {
                pBox.SizeChanged -= pBox_FastDisplaySizeChanged;
                displaySizingInitialized = false;
            }

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
