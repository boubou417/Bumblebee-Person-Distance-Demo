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
        // Balance test between the previous 33 ms and 20 ms display pump.
        // 20 ms reached about 32 FPS but slightly reduced Pose FPS and increased
        // CPU usage. A 25 ms cadence should keep the UI smooth while returning
        // more CPU time to ONNX Runtime pose inference.
        private const int DisplayPumpPeriodMs = 25;

        private readonly object displayFrameLock = new object();
        private DisplayFrame pendingDisplayFrame;
        private DisplayFrame currentDisplayFrame;
        private System.Threading.Timer displayPumpTimer;
        private bool displayPumpRunning;
        private int displayPumpInvokePending;
        private int displayedFrameCounter;
        private int displayTargetWidth;
        private int displayTargetHeight;
        private int displaySizingInvokePending;
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

        private void RequestFastDisplaySizingInitialization()
        {
            if (displaySizingInitialized || IsDisposed || !IsHandleCreated)
                return;

            if (Interlocked.CompareExchange(ref displaySizingInvokePending, 1, 0) != 0)
                return;

            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        InitializeFastDisplaySizing();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref displaySizingInvokePending, 0);
                    }
                }));
            }
            catch
            {
                Interlocked.Exchange(ref displaySizingInvokePending, 0);
            }
        }

        private void EnsureFastDisplayPump()
        {
            RequestFastDisplaySizingInitialization();

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

        private static int Align24BppWidth(int width)
        {
            // Bitmap(width, height, stride, Format24bppRgb, scan0) requires a
            // DWORD-aligned stride. An OpenCV CV_8UC3 Mat normally has
            // stride = width * 3, so keeping width divisible by four guarantees
            // a stride divisible by four as well.
            if (width < 4)
                return width;

            int aligned = width - (width % 4);
            return Math.Max(4, aligned);
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

            // On the first frame the target size can still be the native camera
            // size. After the UI has initialized, the second frame may use a
            // non-DWORD-aligned PictureBox width. That produces a CV_8UC3 Mat
            // whose Step is not valid for the GDI+ 24-bpp Bitmap constructor.
            // Align the display width before Resize so the zero-copy Bitmap stays
            // valid without adding an expensive row-by-row copy.
            int alignedWidth = Align24BppWidth(outputWidth);
            if (alignedWidth != outputWidth && alignedWidth >= 4)
            {
                outputWidth = alignedWidth;
                double alignedScale = outputWidth / (double)source.Width;
                outputHeight = Math.Max(
                    1,
                    (int)Math.Round(source.Height * alignedScale));
            }

            Mat frameMat = new Mat();

            try
            {
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

                int stride = (int)frameMat.Step;
                if ((stride & 3) != 0)
                {
                    throw new InvalidOperationException(
                        "Display frame stride is not DWORD aligned. " +
                        "Size=" + frameMat.Width + "x" + frameMat.Height +
                        ", Step=" + stride + ".");
                }

                Bitmap frameBitmap = new Bitmap(
                    frameMat.Width,
                    frameMat.Height,
                    stride,
                    PixelFormat.Format24bppRgb,
                    frameMat.DataPointer);

                DisplayFrame newFrame = new DisplayFrame(frameMat, frameBitmap);
                frameMat = null;
                DisplayFrame oldPending;

                lock (displayFrameLock)
                {
                    oldPending = pendingDisplayFrame;
                    pendingDisplayFrame = newFrame;
                }

                oldPending?.Dispose();
            }
            finally
            {
                // Ownership is transferred to DisplayFrame on success. If Resize
                // or Bitmap construction fails, release the temporary Mat here.
                frameMat?.Dispose();
            }
        }

        private void DisplayPumpTick(object state)
        {
            if (!displayPumpRunning || IsDisposed || !IsHandleCreated)
                return;

            // Keep at most one UI callback pending. The timer therefore does not
            // accumulate stale BeginInvoke work if the UI thread is momentarily
            // busy; the next callback simply presents the latest available frame.
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
            Interlocked.Exchange(ref displaySizingInvokePending, 0);

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
