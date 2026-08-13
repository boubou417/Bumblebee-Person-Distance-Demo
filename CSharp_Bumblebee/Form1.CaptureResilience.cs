using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Emgu.CV;
using Emgu.CV.CvEnum;
using SpinnakerNET;

namespace CSharp_Bumblebee
{
    public partial class Form1
    {
        // A single bad frame should not stop an exhibition demo. Transport/payload
        // failures are allowed a shorter retry window than processing/display errors.
        private const int CaptureMaxConsecutiveTransportErrors = 8;
        private const int CaptureMaxConsecutiveFrameErrors = 20;

        private readonly object captureDiagnosticsLock = new object();
        private int captureRecoverableErrorCount;
        private int captureIncompleteFrameCount;
        private string captureCurrentStage = "Idle";
        private string captureLastErrorStage = "None";
        private string captureLastErrorMessage = "None";
        private string captureLogPath = string.Empty;

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
            WriteCaptureLog("Capture worker started.");

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

                            // An incomplete synchronized stereo payload is a dropped
                            // frame, not a reason to stop acquisition.
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

                        // Release camera-owned buffers as soon as the deep copies are
                        // complete. The CPU-heavy pose/display work should not retain
                        // Bumblebee stream buffers.
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

                            if ((poseFrameCounter % PoseSourceInterval) == 0)
                            {
                                SetCaptureStage("PoseQueue");
                                sw.Restart();
                                QueuePoseFrame(bgr);
                                sw.Stop();
                                SetPosePrepMs(sw.Elapsed.TotalMilliseconds);
                            }

                            SetCaptureStage("DistanceDraw");
                            unsafe
                            {
                                sw.Restart();
                                DrawCachedPoseAndDistance(
                                    (ushort*)disparityImg.NativeData,
                                    bgr);
                                sw.Stop();
                                SetDistanceRenderMs(sw.Elapsed.TotalMilliseconds);
                            }

                            poseFrameCounter++;

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

                        bool tooManyTransportErrors =
                            transportError &&
                            consecutiveTransportErrors >= CaptureMaxConsecutiveTransportErrors;
                        bool tooManyFrameErrors =
                            !transportError &&
                            consecutiveFrameErrors >= CaptureMaxConsecutiveFrameErrors;

                        if (tooManyTransportErrors || tooManyFrameErrors)
                        {
                            terminalException = ex;
                            terminalStage = failedStage;
                            break;
                        }

                        // Back off only for camera/transport failures. Processing
                        // errors should simply skip the bad frame and use the next one.
                        if (transportError)
                        {
                            int delayMs = Math.Min(100, 10 * consecutiveTransportErrors);
                            Thread.Sleep(delayMs);
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
                StopPoseWorker();
                rectifiedImg.Dispose();
                disparityImg.Dispose();
                distanceTracks.Clear();
                capImgComplete = true;

                e.Result = new CaptureWorkerResult
                {
                    Error = terminalException,
                    Stage = terminalStage,
                    LogPath = captureLogPath,
                    RecoverableErrors = Volatile.Read(ref captureRecoverableErrorCount),
                    IncompleteFrames = Volatile.Read(ref captureIncompleteFrameCount)
                };

                WriteCaptureLog(
                    terminalException == null
                        ? "Capture worker stopped normally."
                        : "Capture worker stopped after repeated errors at stage " +
                          terminalStage + ".");

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

            string logInfo = string.IsNullOrWhiteSpace(result.LogPath)
                ? string.Empty
                : "\r\n\r\nLog: " + result.LogPath;

            MessageBox.Show(
                "Capture stopped after repeated errors.\r\n\r\n" +
                "Stage: " + result.Stage + "\r\n" +
                "Type: " + result.Error.GetType().FullName + "\r\n" +
                "Message: " + result.Error.Message + "\r\n" +
                "Recoverable errors: " + result.RecoverableErrors + "\r\n" +
                "Incomplete frames: " + result.IncompleteFrames +
                logInfo,
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
                captureLogPath = string.Empty;

                try
                {
                    string logDirectory = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "Logs");
                    Directory.CreateDirectory(logDirectory);
                    captureLogPath = Path.Combine(
                        logDirectory,
                        "capture_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                }
                catch
                {
                    // Logging must never become another reason for acquisition to stop.
                    captureLogPath = string.Empty;
                }
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

            WriteCaptureLog(
                "ERROR Stage=" + (stage ?? "Unknown") +
                ", Consecutive=" + consecutiveCount +
                Environment.NewLine +
                (exception != null ? exception.ToString() : "Unknown error"));
        }

        private void WriteCaptureLog(string message)
        {
            string path;
            lock (captureDiagnosticsLock)
                path = captureLogPath;

            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                lock (captureDiagnosticsLock)
                {
                    File.AppendAllText(
                        path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                        "  " + message + Environment.NewLine,
                        System.Text.Encoding.UTF8);
                }
            }
            catch
            {
            }
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
        public string LogPath;
        public int RecoverableErrors;
        public int IncompleteFrames;
    }
}
