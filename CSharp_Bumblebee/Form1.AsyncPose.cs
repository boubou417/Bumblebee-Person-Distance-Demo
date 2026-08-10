using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Emgu.CV;
using Emgu.CV.Dnn;
using Emgu.CV.Util;
using Microsoft.ML.OnnxRuntime;

namespace CSharp_Bumblebee
{
    public partial class Form1
    {
        private const int OrtIntraOpThreads = 3;
        private const int OrtInterOpThreads = 1;
        private const string PoseBackendName = "ORT CPU I3";

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

            lock (poseFrameLock)
            {
                pendingPoseWorkItem?.Dispose();
                pendingPoseWorkItem = null;
            }

            poseInferenceCounter = 0;
            poseWorkerRunning = true;
            poseWorkerThread = new Thread(PoseWorkerLoop)
            {
                IsBackground = true,
                Name = "BumblebeePoseWorkerORT",
                Priority = ThreadPriority.BelowNormal
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

            // Keep at most one prepared frame waiting behind the frame currently
            // being inferred. The camera is much faster than CPU pose inference.
            lock (poseFrameLock)
            {
                if (pendingPoseWorkItem != null)
                    return;
            }

            Stopwatch prep = Stopwatch.StartNew();

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

            bool queued = false;

            lock (poseFrameLock)
            {
                if (poseWorkerRunning && pendingPoseWorkItem == null)
                {
                    pendingPoseWorkItem = item;
                    queued = true;
                }
            }

            prep.Stop();

            if (!queued)
            {
                item.Dispose();
                return;
            }

            SetPosePrepMs(prep.Elapsed.TotalMilliseconds);
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
            SessionOptions sessionOptions = null;
            InferenceSession session = null;
            RunOptions runOptions = null;
            OrtValue inputValue = null;
            OrtValue outputValue = null;

            try
            {
                sessionOptions = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    IntraOpNumThreads = OrtIntraOpThreads,
                    InterOpNumThreads = OrtInterOpThreads
                };

                session = new InferenceSession(PoseModelFile, sessionOptions);
                runOptions = new RunOptions();

                string inputName = session.InputNames.First();
                string outputName = session.OutputNames.First();

                int[] outputDimensions = session.OutputMetadata[outputName].Dimensions;
                ValidateOrtOutputShape(outputDimensions);

                long[] inputShape = { 1, 3, YoloSize, YoloSize };
                long[] outputShape = Array.ConvertAll(
                    outputDimensions,
                    dimension => (long)dimension);

                int outputElementCount = 1;
                foreach (int dimension in outputDimensions)
                    outputElementCount = checked(outputElementCount * dimension);

                float[] inputBuffer = new float[3 * YoloSize * YoloSize];
                float[] outputBuffer = new float[outputElementCount];

                inputValue = OrtValue.CreateTensorValueFromMemory(inputBuffer, inputShape);
                outputValue = OrtValue.CreateTensorValueFromMemory(outputBuffer, outputShape);

                string[] inputNames = { inputName };
                string[] outputNames = { outputName };
                OrtValue[] inputValues = { inputValue };
                OrtValue[] outputValues = { outputValue };

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
                        Stopwatch tensorPrep = Stopwatch.StartNew();
                        FillOrtInputTensor(item.Input, inputBuffer);
                        tensorPrep.Stop();
                        SetTensorPrepMs(tensorPrep.Elapsed.TotalMilliseconds);

                        Stopwatch forward = Stopwatch.StartNew();
                        session.Run(
                            runOptions,
                            inputNames,
                            inputValues,
                            outputNames,
                            outputValues);
                        forward.Stop();
                        SetInferenceMs(forward.Elapsed.TotalMilliseconds);

                        Stopwatch post = Stopwatch.StartNew();
                        UpdatePoseResultsFromOrt(
                            outputBuffer,
                            outputDimensions,
                            item);
                        post.Stop();
                        SetPosePostMs(post.Elapsed.TotalMilliseconds);

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
                                "YOLO Pose worker error (ONNX Runtime CPU):\r\n" +
                                ex,
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
                outputValue?.Dispose();
                inputValue?.Dispose();
                runOptions?.Dispose();
                session?.Dispose();
                sessionOptions?.Dispose();
            }
        }

        private static void ValidateOrtOutputShape(int[] dimensions)
        {
            if (dimensions == null || dimensions.Length != 3)
                throw new InvalidOperationException(
                    "Unexpected YOLO Pose output rank. Expected 3 dimensions.");

            foreach (int dimension in dimensions)
            {
                if (dimension <= 0)
                {
                    throw new InvalidOperationException(
                        "The ONNX Runtime benchmark requires a fixed output shape.");
                }
            }

            if (dimensions[1] != PoseOutputChannels &&
                dimensions[2] != PoseOutputChannels)
            {
                throw new InvalidOperationException(
                    "Unexpected YOLO Pose output shape. Expected one dimension to be " +
                    PoseOutputChannels + ".");
            }
        }

        private unsafe void FillOrtInputTensor(Mat input, float[] tensorData)
        {
            if (input == null || input.IsEmpty ||
                input.Width != YoloSize || input.Height != YoloSize)
            {
                throw new InvalidOperationException(
                    "Pose input must be a " + YoloSize + "x" + YoloSize + " BGR image.");
            }

            int planeSize = YoloSize * YoloSize;
            if (tensorData == null || tensorData.Length != planeSize * 3)
                throw new ArgumentException("Invalid ONNX Runtime input tensor buffer.");

            byte* sourceBase = (byte*)input.DataPointer.ToPointer();
            int sourceStep = (int)input.Step;
            const float normalizer = 1.0f / 255.0f;

            fixed (float* destination = tensorData)
            {
                float* red = destination;
                float* green = destination + planeSize;
                float* blue = destination + planeSize * 2;

                for (int y = 0; y < YoloSize; y++)
                {
                    byte* row = sourceBase + y * sourceStep;
                    int rowOffset = y * YoloSize;

                    for (int x = 0; x < YoloSize; x++)
                    {
                        int pixelOffset = x * 3;
                        int tensorIndex = rowOffset + x;

                        // Mat is BGR; YOLO input is normalized RGB in NCHW order.
                        red[tensorIndex] = row[pixelOffset + 2] * normalizer;
                        green[tensorIndex] = row[pixelOffset + 1] * normalizer;
                        blue[tensorIndex] = row[pixelOffset] * normalizer;
                    }
                }
            }
        }

        private void UpdatePoseResultsFromOrt(
            float[] output,
            int[] dimensions,
            PoseWorkItem item)
        {
            bool channelsFirst = dimensions[1] == PoseOutputChannels;
            int candidateCount = channelsFirst
                ? dimensions[2]
                : dimensions[1];

            List<PosePerson> people = new List<PosePerson>();
            List<Rectangle> boxes = new List<Rectangle>();
            List<float> scores = new List<float>();

            for (int i = 0; i < candidateCount; i++)
            {
                float score = GetOrtOutputValue(
                    output,
                    candidateCount,
                    channelsFirst,
                    i,
                    4);

                if (score < ConfidenceThreshold)
                    continue;

                float cx = (
                    GetOrtOutputValue(output, candidateCount, channelsFirst, i, 0) -
                    item.PadX) / item.Scale;
                float cy = (
                    GetOrtOutputValue(output, candidateCount, channelsFirst, i, 1) -
                    item.PadY) / item.Scale;
                float bw = GetOrtOutputValue(
                    output,
                    candidateCount,
                    channelsFirst,
                    i,
                    2) / item.Scale;
                float bh = GetOrtOutputValue(
                    output,
                    candidateCount,
                    channelsFirst,
                    i,
                    3) / item.Scale;

                Rectangle box = ClampRect(
                    new Rectangle(
                        (int)(cx - bw / 2),
                        (int)(cy - bh / 2),
                        (int)bw,
                        (int)bh),
                    item.SourceWidth,
                    item.SourceHeight);

                if (box.IsEmpty)
                    continue;

                PosePerson person = new PosePerson
                {
                    Box = box,
                    Keypoints = new PoseKeypoint[KeypointCount]
                };

                for (int k = 0; k < KeypointCount; k++)
                {
                    int channel = 5 + k * 3;
                    person.Keypoints[k] = new PoseKeypoint
                    {
                        X = (
                            GetOrtOutputValue(
                                output,
                                candidateCount,
                                channelsFirst,
                                i,
                                channel) - item.PadX) / item.Scale,
                        Y = (
                            GetOrtOutputValue(
                                output,
                                candidateCount,
                                channelsFirst,
                                i,
                                channel + 1) - item.PadY) / item.Scale,
                        Confidence = GetOrtOutputValue(
                            output,
                            candidateCount,
                            channelsFirst,
                            i,
                            channel + 2)
                    };
                }

                people.Add(person);
                boxes.Add(box);
                scores.Add(score);
            }

            List<PosePerson> selectedPeople = new List<PosePerson>();

            if (people.Count > 0)
            {
                using (VectorOfRect bv = new VectorOfRect(boxes.ToArray()))
                using (VectorOfFloat sv = new VectorOfFloat(scores.ToArray()))
                using (VectorOfInt indices = new VectorOfInt())
                {
                    DnnInvoke.NMSBoxes(
                        bv,
                        sv,
                        ConfidenceThreshold,
                        NmsThreshold,
                        indices);

                    foreach (int index in indices.ToArray())
                        selectedPeople.Add(people[index]);
                }
            }

            lock (poseResultLock)
            {
                cachedPosePeople.Clear();
                cachedPosePeople.AddRange(selectedPeople);
            }
        }

        private static float GetOrtOutputValue(
            float[] output,
            int candidateCount,
            bool channelsFirst,
            int candidate,
            int channel)
        {
            return channelsFirst
                ? output[channel * candidateCount + candidate]
                : output[candidate * PoseOutputChannels + channel];
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
