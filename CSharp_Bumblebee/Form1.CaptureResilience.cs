using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using Emgu.CV;
using Emgu.CV.CvEnum;
using SpinnakerNET;

namespace CSharp_Bumblebee
{
    public partial class Form1
    {
        // Processing/display errors are still bounded so a real software fault does
        // not spin forever. Camera transport/payload/copy errors are retried without
        // a stop threshold because exhibition acquisition should survive intermittent
        // synchronized-image timeouts. GigE tuning is handled separately.
        private const int CaptureMaxConsecutiveFrameErrors = 20;

        private readonly object captureDiagnosticsLock = new object();
        private int captureRecoverableErrorCount;
        private int captureIncompleteFrameCount;
        private string captureCurrentStage = "Idle";
        private string captureLastErrorStage = "None";
        private string captureLastErrorMessage = "None";

        private void backgroundWorker1_DoWork_Resilient(
            object sender,
            DoWorkEventArgs e)
        {
            IManagedImage rectifiedImg = new ManagedImage();
            IManagedImage disparityImg = new ManagedImage();
            Stopwatch sw = new Stopwatch();

            int consecutiveTransportErrors = 0;
            int consecutiveFrameErrors = 0;
            Exception terminalException = null;
            string terminalStage = "None";

            BeginCaptureDiagnostics();

            try
            {
                while (capImg)
                {
                    IManagedImageList imageList = null;
                    IManagedImage rectifiedSource = null;
                    IManagedImage disparitySource = null;
                    bool frameSucceeded = false;

                    try
                    {
                        SetCaptureStage("Capture");
                        sw.Restart();
                        imageList = cam.GetNextImageSync(3000);
                        sw.Stop();
                        SetCaptureMs(sw.Elapsed.TotalMilliseconds);

                        if (imageList == null)
                            throw new InvalidOperationException("GetNextImageSync returned null.");

                        SetCaptureStage("Payload");
                        rectifiedSource = imageList.GetByPayloadType(
                            ImagePayloadType.IMAGE_PAYLOAD_TYPE_RECTIFIED_SENSOR1);
                        disparitySource = imageList.GetByPayloadType(
                            ImagePayloadType.IMAGE_PAYLOAD_TYPE_DISPARITY_SENSOR1);

                        if (rectifiedSource == null ||
                            disparitySource == null ||
                            rectifiedSource.IsIncomplete ||
                            disparitySource.IsIncomplete)
                        {
                            Interlocked.Increment(ref captureIncompleteFrameCount);

                            // A dropped synchronized stereo set is recoverable. Release
                            // it and wait for the next complete image set.
                            consecutiveTransportErrors = 0;
                            consecutiveFrameErrors = 0;
                            continue;
                        }

                        SetCaptureStage("Copy");
                        sw.Restart();
                        rectifiedImg.DeepCopy(rectifiedSource);
                        disparityImg.DeepCopy(disparitySource);
                        sw.Stop();
                        SetCopyMs(sw.Elapsed.TotalMilliseconds);

                        // Release camera-owned buffers immediately after DeepCopy so
                        // detection inference and display never hold transport buffers.
                        SafeDisposeImage(ref rectifiedSource);
                        SafeDisposeImage(ref disparitySource);
                        SafeReleaseImageList(ref imageList);

                        int width = (int)rectifiedImg.Width;
                        int height = (int)rectifiedImg.Height;

                        SetCaptureStage("Convert");
                        using (Mat bgr = new Mat())
                        using (Mat rgb = new Mat(
                            height,
                            width,
                            DepthType.Cv8U,
                            3,
                            rectifiedImg.DataPtr,
                            0))
                        {
                            sw.Restart();
                            CvInvoke.CvtColor(rgb, bgr, ColorConversion.Rgb2Bgr);
                            sw.Stop();
                            SetConvertMs(sw.Elapsed.TotalMilliseconds);

                            if ((cameraFrameCounter % DetectionSourceInterval) == 0)
                            {
                                SetCaptureStage("DetectionQueue");
                                sw.Restart();
                                QueueDetectionFrame(bgr);
                                sw.Stop();
                                SetDetectionPrepMs(sw.Elapsed.TotalMilliseconds);
                            }

                            SetCaptureStage("Distance");
                            unsafe
                            {
                                sw.Restart();
                                DrawCachedDetectionAndDistance(
                                    (ushort*)disparityImg.NativeData,
                                    bgr);
                                sw.Stop();
                                SetDistanceRenderMs(sw.Elapsed.TotalMilliseconds);
                            }

                            cameraFrameCounter++;

                            SetCaptureStage("DisplayQueue");
                            sw.Restart();
                            QueueDisplayFrame(bgr);
                            sw.Stop();
                            SetBitmapQueueMs(sw.Elapsed.TotalMilliseconds);
                        }

                        frameSucceeded = true;
                        consecutiveTransportErrors = 0;
                        consecutiveFrameErrors = 0;
                        SetCaptureStage("Running");
                    }
                    catch (Exception ex)
                    {
                        string failedStage = GetCaptureStage();
                        bool transportError = IsCaptureTransportStage(failedStage);

                        if (transportError)
                            consecutiveTransportErrors++;
                        else
                            consecutiveFrameErrors++;

                        RecordCaptureException(
                            failedStage,
                            ex,
                            transportError
                                ? consecutiveTransportErrors
                                : consecutiveFrameErrors);

                        if (!capImg)
                            break;

                        if (transportError)
                        {
                            // GetNextImageSync timeout, incomplete transport, and
                            // transient copy errors never stop acquisition. Skip the
                            // bad synchronized set and continue with a short backoff.
                            int delayMs = Math.Min(
                                100,
                                10 * Math.Max(1, consecutiveTransportErrors));
                            Thread.Sleep(delayMs);
                            continue;
                        }

                        if (consecutiveFrameErrors >= CaptureMaxConsecutiveFrameErrors)
                        {
                            terminalException = ex;
                            terminalStage = failedStage;
                            break;
                        }
                    }
                    finally
                    {
                        SafeDisposeImage(ref rectifiedSource);
                        SafeDisposeImage(ref disparitySource);
                        SafeReleaseImageList(ref imageList);

                        if (!frameSucceeded && !capImg)
                            SetCaptureStage("Stopping");
                    }
                }
            }
            catch (Exception ex)
            {
                terminalException = ex;
                terminalStage = GetCaptureStage();
                RecordCaptureException(terminalStage, ex, -1);
            }
            finally
            {
                SetCaptureStage("Stopping");
                StopDetectionWorker();
                rectifiedImg.Dispose();
                disparityImg.Dispose();
                distanceTracks.Clear();
                capImgComplete = true;

                e.Result = new CaptureWorkerResult
                {
                    Error = terminalException,
                    Stage = terminalStage,
                    RecoverableErrors = Volatile.Read(ref captureRecoverableErrorCount),
                    IncompleteFrames = Volatile.Read(ref captureIncompleteFrameCount)
                };

                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            cam.EndAcquisition();
                        }
                        catch (Exception endEx)
                        {
                            RecordCaptureException("EndAcquisition", endEx, -1);
                        }

                        startBtn.Text = "開始取像";
                        startBtn.Enabled = true;
                        connectBtn.Enabled = true;
                        started = false;
                    }));
                }
                catch
                {
                }
            }
        }

        private void backgroundWorker1_RunWorkerCompleted_Resilient(
            object sender,
            RunWorkerCompletedEventArgs e)
        {
            if (e.Cancelled)
                return;

            if (e.Error != null)
            {
                MessageBox.Show(
                    "Capture worker terminated unexpectedly.\r\n\r\n" + e.Error,
                    "Capture Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            CaptureWorkerResult result = e.Result as CaptureWorkerResult;
            if (result == null || result.Error == null)
                return;

            MessageBox.Show(
                "Capture stopped after repeated processing errors.\r\n\r\n" +
                "Stage: " + result.Stage + "\r\n" +
                "Type: " + result.Error.GetType().FullName + "\r\n" +
                "Message: " + result.Error.Message + "\r\n" +
                "Recoverable errors: " + result.RecoverableErrors + "\r\n" +
                "Incomplete frames: " + result.IncompleteFrames,
                "Capture Diagnostics",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private void BeginCaptureDiagnostics()
        {
            Interlocked.Exchange(ref captureRecoverableErrorCount, 0);
            Interlocked.Exchange(ref captureIncompleteFrameCount, 0);

            lock (captureDiagnosticsLock)
            {
                captureCurrentStage = "Starting";
                captureLastErrorStage = "None";
                captureLastErrorMessage = "None";
            }
        }

        private void SetCaptureStage(string stage)
        {
            lock (captureDiagnosticsLock)
                captureCurrentStage = stage ?? "Unknown";
        }

        private string GetCaptureStage()
        {
            lock (captureDiagnosticsLock)
                return captureCurrentStage;
        }

        private void RecordCaptureException(
            string stage,
            Exception exception,
            int consecutiveCount)
        {
            Interlocked.Increment(ref captureRecoverableErrorCount);

            string message = exception == null
                ? "Unknown error"
                : exception.GetType().Name + ": " + exception.Message;

            lock (captureDiagnosticsLock)
            {
                captureLastErrorStage = stage ?? "Unknown";
                captureLastErrorMessage = message;
            }

            // File logging is intentionally disabled. Keep only lightweight
            // in-memory diagnostics for the F3 overlay.
        }

        private static bool IsCaptureTransportStage(string stage)
        {
            return string.Equals(stage, "Capture", StringComparison.Ordinal) ||
                   string.Equals(stage, "Payload", StringComparison.Ordinal) ||
                   string.Equals(stage, "Copy", StringComparison.Ordinal);
        }

        private static void SafeDisposeImage(ref IManagedImage image)
        {
            IManagedImage current = image;
            image = null;

            if (current == null)
                return;

            try
            {
                current.Dispose();
            }
            catch
            {
            }
        }

        private static void SafeReleaseImageList(ref IManagedImageList imageList)
        {
            IManagedImageList current = imageList;
            imageList = null;

            if (current == null)
                return;

            try
            {
                current.Release();
            }
            catch
            {
            }
        }

        private string GetCaptureDiagnosticsOverlayText()
        {
            int errors = Volatile.Read(ref captureRecoverableErrorCount);
            int incomplete = Volatile.Read(ref captureIncompleteFrameCount);
            string stage;
            string lastStage;
            string lastMessage;

            lock (captureDiagnosticsLock)
            {
                stage = captureCurrentStage;
                lastStage = captureLastErrorStage;
                lastMessage = captureLastErrorMessage;
            }

            if (lastMessage != null && lastMessage.Length > 42)
                lastMessage = lastMessage.Substring(0, 39) + "...";

            return "CaptureStage: " + stage + Environment.NewLine +
                   "CaptureErr  : " + errors + Environment.NewLine +
                   "Incomplete  : " + incomplete + Environment.NewLine +
                   "LastErr     : " + lastStage +
                   (errors > 0 ? " / " + lastMessage : string.Empty);
        }
    }

    internal sealed class CaptureWorkerResult
    {
        public Exception Error;
        public string Stage;
        public int RecoverableErrors;
        public int IncompleteFrames;
    }
}
