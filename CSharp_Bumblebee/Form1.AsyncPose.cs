using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
        private const string PoseBackendName = "ORT CPU DET I3";
        private const string PreferredDetectionModelFile = "yolov8n-512.onnx";
        private const string FallbackDetectionModelFile = "yolov8n.onnx";

        private readonly object poseFrameLock = new object();
        private readonly object poseResultLock = new object();
        private readonly AutoResetEvent poseFrameReady = new AutoResetEvent(false);

        private Thread poseWorkerThread;
        private volatile bool poseWorkerRunning;
        private PoseWorkItem pendingPoseWorkItem;
        private int poseInferenceCounter;

        private SessionOptions detectionSessionOptions;
        private InferenceSession detectionSession;
        private RunOptions detectionRunOptions;
        private OrtValue detectionInputValue;
        private OrtValue detectionOutputValue;
        private string detectionInputName;
        private string detectionOutputName;
        private int[] detectionOutputDimensions;
        private int detectionInputSize = 512;
        private int detectionOutputChannels;
        private int detectionCandidateCount;
        private bool detectionChannelsFirst;
        private float[] detectionInputBuffer;
        private float[] detectionOutputBuffer;
        private string detectionModelLabel = "not loaded";

        private void StartPoseWorker()
        {
            StopPoseWorker();

            lock (poseResultLock)
            {
                cachedPosePeople.Clear();
            }

            ResetPoseTemporalFilter();

            lock (poseFrameLock)
            {
                pendingPoseWorkItem?.Dispose();
                pendingPoseWorkItem = null;
            }

            poseInferenceCounter = 0;

            try
            {
                InitializeDetectionSession();
            }
            catch (Exception ex)
            {
                DisposeDetectionSession();
                capImg = false;

                MessageBox.Show(
                    "YOLO person detection model could not be initialized.\r\n\r\n" +
                    "Preferred: " + PreferredDetectionModelFile + "\r\n" +
                    "Fallback : " + FallbackDetectionModelFile + "\r\n\r\n" +
                    ex.Message,
                    "Detection Model Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            poseWorkerRunning = true;
            poseWorkerThread = new Thread(PoseWorkerLoop)
            {
                IsBackground = true,
                Name = "BumblebeeDetectionWorkerORT",
                Priority = ThreadPriority.BelowNormal
            };
            poseWorkerThread.Start();
        }

        private void InitializeDetectionSession()
        {
            string modelPath = null;

            if (File.Exists(PreferredDetectionModelFile))
                modelPath = PreferredDetectionModelFile;
            else if (File.Exists(FallbackDetectionModelFile))
                modelPath = FallbackDetectionModelFile;

            if (string.IsNullOrWhiteSpace(modelPath))
            {
                throw new FileNotFoundException(
                    "No YOLO detection ONNX model was found in the application folder.");
            }

            detectionSessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                IntraOpNumThreads = OrtIntraOpThreads,
                InterOpNumThreads = OrtInterOpThreads
            };

            detectionSession = new InferenceSession(modelPath, detectionSessionOptions);
            detectionRunOptions = new RunOptions();
            detectionInputName = detectionSession.InputNames.First();
            detectionOutputName = detectionSession.OutputNames.First();

            int[] inputDimensions =
                detectionSession.InputMetadata[detectionInputName].Dimensions;

            if (inputDimensions == null ||
                inputDimensions.Length != 4 ||
                inputDimensions[2] <= 0 ||
                inputDimensions[3] <= 0 ||
                inputDimensions[2] != inputDimensions[3])
            {
                throw new InvalidOperationException(
                    "Detection model must have a fixed square NCHW input shape.");
            }

            detectionInputSize = inputDimensions[2];
            detectionOutputDimensions =
                detectionSession.OutputMetadata[detectionOutputName].Dimensions;

            ValidateDetectionOutputShape(detectionOutputDimensions);

            detectionChannelsFirst =
                detectionOutputDimensions[1] >= 5 &&
                detectionOutputDimensions[1] <= 256;

            detectionOutputChannels = detectionChannelsFirst
                ? detectionOutputDimensions[1]
                : detectionOutputDimensions[2];
            detectionCandidateCount = detectionChannelsFirst
                ? detectionOutputDimensions[2]
                : detectionOutputDimensions[1];

            long[] inputShape =
            {
                1,
                3,
                detectionInputSize,
                detectionInputSize
            };

            long[] outputShape = Array.ConvertAll(
                detectionOutputDimensions,
                dimension => (long)dimension);

            int inputElementCount = checked(
                3 * detectionInputSize * detectionInputSize);
            int outputElementCount = 1;

            foreach (int dimension in detectionOutputDimensions)
                outputElementCount = checked(outputElementCount * dimension);

            detectionInputBuffer = new float[inputElementCount];
            detectionOutputBuffer = new float[outputElementCount];
            detectionInputValue = OrtValue.CreateTensorValueFromMemory(
                detectionInputBuffer,
                inputShape);
            detectionOutputValue = OrtValue.CreateTensorValueFromMemory(
                detectionOutputBuffer,
                outputShape);

            detectionModelLabel =
                Path.GetFileName(modelPath) + " / " + detectionInputSize + "px";
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

            DisposeDetectionSession();
        }

        private void DisposeDetectionSession()
        {
            detectionOutputValue?.Dispose();
            detectionOutputValue = null;
            detectionInputValue?.Dispose();
            detectionInputValue = null;
            detectionRunOptions?.Dispose();
            detectionRunOptions = null;
            detectionSession?.Dispose();
            detectionSession = null;
            detectionSessionOptions?.Dispose();
            detectionSessionOptions = null;
            detectionInputBuffer = null;
            detectionOutputBuffer = null;
            detectionOutputDimensions = null;
            detectionInputName = null;
            detectionOutputName = null;
        }

        private void QueuePoseFrame(Mat source)
        {
            if (!poseWorkerRunning || source == null || source.IsEmpty)
                return;

            // Keep at most one prepared frame waiting behind the frame currently
            // being inferred. This prevents CPU inference from backing up capture.
            lock (poseFrameLock)
            {
                if (pendingPoseWorkItem != null)
                    return;
            }

            Stopwatch prep = Stopwatch.StartNew();

            float scale;
            int padX;
            int padY;
            Mat input = LetterboxDetection(
                source,
                detectionInputSize,
                out scale,
                out padX,
                out padY);

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

        private Mat LetterboxDetection(
            Mat source,
            int targetSize,
            out float scale,
            out int padX,
            out int padY)
        {
            scale = Math.Min(
                targetSize / (float)source.Width,
                targetSize / (float)source.Height);

            int resizedWidth = Math.Max(
                1,
                (int)Math.Round(source.Width * scale));
            int resizedHeight = Math.Max(
                1,
                (int)Math.Round(source.Height * scale));

            padX = (targetSize - resizedWidth) / 2;
            padY = (targetSize - resizedHeight) / 2;

            Mat destination = new Mat(
                targetSize,
                targetSize,
                Emgu.CV.CvEnum.DepthType.Cv8U,
                3);
            destination.SetTo(new Emgu.CV.Structure.MCvScalar(114, 114, 114));

            using (Mat resized = new Mat())
            {
                CvInvoke.Resize(
                    source,
                    resized,
                    new Size(resizedWidth, resizedHeight));

                using (Mat roi = new Mat(
                    destination,
                    new Rectangle(
                        padX,
                        padY,
                        resizedWidth,
                        resizedHeight)))
                {
                    resized.CopyTo(roi);
                }
            }

            return destination;
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
            try
            {
                string[] inputNames = { detectionInputName };
                string[] outputNames = { detectionOutputName };
                OrtValue[] inputValues = { detectionInputValue };
                OrtValue[] outputValues = { detectionOutputValue };

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
                        FillDetectionInputTensor(
                            item.Input,
                            detectionInputBuffer,
                            detectionInputSize);
                        tensorPrep.Stop();
                        SetTensorPrepMs(tensorPrep.Elapsed.TotalMilliseconds);

                        Stopwatch forward = Stopwatch.StartNew();
                        detectionSession.Run(
                            detectionRunOptions,
                            inputNames,
                            inputValues,
                            outputNames,
                            outputValues);
                        forward.Stop();
                        SetInferenceMs(forward.Elapsed.TotalMilliseconds);

                        Stopwatch post = Stopwatch.StartNew();
                        UpdateDetectionResultsFromOrt(item);
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

                lock (poseResultLock)
                    cachedPosePeople.Clear();

                if (!IsDisposed && IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                            MessageBox.Show(
                                "YOLO person detection worker error " +
                                "(ONNX Runtime CPU):\r\n" + ex,
                                "Detection Error",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error)));
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void ValidateDetectionOutputShape(int[] dimensions)
        {
            if (dimensions == null || dimensions.Length != 3)
            {
                throw new InvalidOperationException(
                    "Unexpected YOLO detection output rank. Expected 3 dimensions.");
            }

            foreach (int dimension in dimensions)
            {
                if (dimension <= 0)
                {
                    throw new InvalidOperationException(
                        "The lightweight detection build requires a fixed output shape.");
                }
            }

            bool channelsFirst =
                dimensions[1] >= 5 && dimensions[1] <= 256;
            bool channelsLast =
                dimensions[2] >= 5 && dimensions[2] <= 256;

            if (!channelsFirst && !channelsLast)
            {
                throw new InvalidOperationException(
                    "Unexpected YOLO detection output shape. " +
                    "Expected a class/channel dimension such as 84.");
            }
        }

        private unsafe void FillDetectionInputTensor(
            Mat input,
            float[] tensorData,
            int inputSize)
        {
            if (input == null || input.IsEmpty ||
                input.Width != inputSize || input.Height != inputSize)
            {
                throw new InvalidOperationException(
                    "Detection input must match the ONNX model input size.");
            }

            int planeSize = inputSize * inputSize;
            if (tensorData == null || tensorData.Length != planeSize * 3)
                throw new ArgumentException("Invalid detection input tensor buffer.");

            byte* sourceBase = (byte*)input.DataPointer.ToPointer();
            int sourceStep = (int)input.Step;
            const float normalizer = 1.0f / 255.0f;

            fixed (float* destination = tensorData)
            {
                float* red = destination;
                float* green = destination + planeSize;
                float* blue = destination + planeSize * 2;

                for (int y = 0; y < inputSize; y++)
                {
                    byte* row = sourceBase + y * sourceStep;
                    int rowOffset = y * inputSize;

                    for (int x = 0; x < inputSize; x++)
                    {
                        int pixelOffset = x * 3;
                        int tensorIndex = rowOffset + x;

                        red[tensorIndex] = row[pixelOffset + 2] * normalizer;
                        green[tensorIndex] = row[pixelOffset + 1] * normalizer;
                        blue[tensorIndex] = row[pixelOffset] * normalizer;
                    }
                }
            }
        }

        private void UpdateDetectionResultsFromOrt(PoseWorkItem item)
        {
            List<PosePerson> people = new List<PosePerson>();
            List<Rectangle> boxes = new List<Rectangle>();
            List<float> scores = new List<float>();

            for (int candidate = 0;
                 candidate < detectionCandidateCount;
                 candidate++)
            {
                // COCO class 0 = person. Ultralytics YOLOv8 detection output is
                // [cx, cy, w, h, class0, class1, ...] with no objectness channel.
                float personScore = GetDetectionOutputValue(candidate, 4);
                if (personScore < ConfidenceThreshold)
                    continue;

                float centerX =
                    (GetDetectionOutputValue(candidate, 0) - item.PadX) /
                    item.Scale;
                float centerY =
                    (GetDetectionOutputValue(candidate, 1) - item.PadY) /
                    item.Scale;
                float width =
                    GetDetectionOutputValue(candidate, 2) / item.Scale;
                float height =
                    GetDetectionOutputValue(candidate, 3) / item.Scale;

                Rectangle box = ClampRect(
                    new Rectangle(
                        (int)Math.Round(centerX - width * 0.5f),
                        (int)Math.Round(centerY - height * 0.5f),
                        (int)Math.Round(width),
                        (int)Math.Round(height)),
                    item.SourceWidth,
                    item.SourceHeight);

                if (box.IsEmpty)
                    continue;

                people.Add(new PosePerson
                {
                    Box = box,
                    // Keep an empty pose array so the existing distance/render path
                    // remains compatible while doing no skeleton work.
                    Keypoints = new PoseKeypoint[KeypointCount]
                });
                boxes.Add(box);
                scores.Add(personScore);
            }

            List<PosePerson> selectedPeople = new List<PosePerson>();

            if (people.Count > 0)
            {
                using (VectorOfRect boxVector = new VectorOfRect(boxes.ToArray()))
                using (VectorOfFloat scoreVector = new VectorOfFloat(scores.ToArray()))
                using (VectorOfInt indices = new VectorOfInt())
                {
                    DnnInvoke.NMSBoxes(
                        boxVector,
                        scoreVector,
                        ConfidenceThreshold,
                        NmsThreshold,
                        indices);

                    foreach (int index in indices.ToArray())
                        selectedPeople.Add(people[index]);
                }
            }

            List<PosePerson> confirmedPeople = UpdateTemporalPoseFilter(
                selectedPeople,
                item.SourceWidth,
                item.SourceHeight);

            lock (poseResultLock)
            {
                cachedPosePeople.Clear();
                cachedPosePeople.AddRange(confirmedPeople);
            }
        }

        private float GetDetectionOutputValue(int candidate, int channel)
        {
            return detectionChannelsFirst
                ? detectionOutputBuffer[
                    channel * detectionCandidateCount + candidate]
                : detectionOutputBuffer[
                    candidate * detectionOutputChannels + channel];
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

        private string GetDetectionModelLabel()
        {
            return detectionModelLabel;
        }

        private int GetDetectionInputSize()
        {
            return detectionInputSize;
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
