using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using Emgu.CV;
using Emgu.CV.Dnn;

namespace CSharp_Bumblebee
{
    public partial class Form1
    {
        private readonly object poseFrameLock = new object();
        private readonly object poseResultLock = new object();
        private readonly AutoResetEvent poseFrameReady = new AutoResetEvent(false);

        private Thread poseWorkerThread;
        private volatile bool poseWorkerRunning;
        private PoseWorkItem pendingPoseWorkItem;
        private int poseInferenceCounter;

        private void StartPoseWorker()
        {
            StopPoseWorker();

            lock (poseResultLock)
            {
                cachedPosePeople.Clear();
            }

            poseInferenceCounter = 0;
            poseWorkerRunning = true;
            poseWorkerThread = new Thread(PoseWorkerLoop)
            {
                IsBackground = true,
                Name = "BumblebeePoseWorker"
            };
            poseWorkerThread.Start();
        }

        private void StopPoseWorker()
        {
            poseWorkerRunning = false;
            poseFrameReady.Set();

            Thread worker = poseWorkerThread;
            if (worker != null && worker.IsAlive && worker != Thread.CurrentThread)
                worker.Join(2000);

            poseWorkerThread = null;

            PoseWorkItem pending = null;
            lock (poseFrameLock)
            {
                pending = pendingPoseWorkItem;
                pendingPoseWorkItem = null;
            }
            pending?.Dispose();
        }

        private void QueuePoseFrame(Mat source)
        {
            if (!poseWorkerRunning || source == null || source.IsEmpty)
                return;

            float scale;
            int padX;
            int padY;
            Mat input = Letterbox(source, out scale, out padX, out padY);

            PoseWorkItem item = new PoseWorkItem(
                input,
                scale,
                padX,
                padY,
                source.Width,
                source.Height);

            PoseWorkItem oldPending;
            lock (poseFrameLock)
            {
                oldPending = pendingPoseWorkItem;
                pendingPoseWorkItem = item;
            }

            oldPending?.Dispose();
            poseFrameReady.Set();
        }

        private PoseWorkItem TakeLatestPoseFrame()
        {
            lock (poseFrameLock)
            {
                PoseWorkItem item = pendingPoseWorkItem;
                pendingPoseWorkItem = null;
                return item;
            }
        }

        private void PoseWorkerLoop()
        {
            Net net = null;

            try
            {
                net = DnnInvoke.ReadNetFromONNX(PoseModelFile);
                net.SetPreferableBackend(Emgu.CV.Dnn.Backend.OpenCV);
                net.SetPreferableTarget(Target.Cpu);

                while (poseWorkerRunning)
                {
                    poseFrameReady.WaitOne(100);
                    if (!poseWorkerRunning)
                        break;

                    PoseWorkItem item = TakeLatestPoseFrame();
                    if (item == null)
                        continue;

                    try
                    {
                        DetectPose(net, item);
                        Interlocked.Increment(ref poseInferenceCounter);
                    }
                    finally
                    {
                        item.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                poseWorkerRunning = false;

                if (!IsDisposed && IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                            MessageBox.Show(
                                "YOLO Pose worker error:\r\n" + ex.Message,
                                "Pose Error",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error)));
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                net?.Dispose();
            }
        }

        private List<PosePerson> GetPoseSnapshot()
        {
            lock (poseResultLock)
            {
                return new List<PosePerson>(cachedPosePeople);
            }
        }

        private int GetPosePeopleCount()
        {
            lock (poseResultLock)
            {
                return cachedPosePeople.Count;
            }
        }
    }

    internal sealed class PoseWorkItem : IDisposable
    {
        public PoseWorkItem(
            Mat input,
            float scale,
            int padX,
            int padY,
            int sourceWidth,
            int sourceHeight)
        {
            Input = input;
            Scale = scale;
            PadX = padX;
            PadY = padY;
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
        }

        public Mat Input { get; private set; }
        public float Scale { get; private set; }
        public int PadX { get; private set; }
        public int PadY { get; private set; }
        public int SourceWidth { get; private set; }
        public int SourceHeight { get; private set; }

        public void Dispose()
        {
            Input?.Dispose();
            Input = null;
        }
    }
}
