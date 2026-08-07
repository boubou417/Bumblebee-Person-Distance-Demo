using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Dnn;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using SpinnakerNET;
using SpinnakerNET.GenApi;

namespace CSharp_Bumblebee
{
    public partial class Form1 : Form
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetDllDirectory(string dllPathName);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("Gdi32.dll", EntryPoint = "CreateRoundRectRgn")]
        private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int ew, int eh);

        private const int YoloSize = 512;
        private const string PoseModelFile = "yolov8n-pose-512.onnx";
        private const float ConfidenceThreshold = 0.50f;
        private const float KeypointThreshold = 0.35f;
        private const float NmsThreshold = 0.45f;
        private const int PoseOutputChannels = 56;
        private const int KeypointCount = 17;
        private const int PoseInferenceInterval = 2;

        private const double DistanceEmaAlpha = 0.25;
        private const double DistanceJumpThresholdMeters = 0.75;
        private const int DistanceTrackMaxMissedFrames = 8;
        private const int DistanceTrackMatchPixels = 140;

        private static readonly int[,] SkeletonEdges =
        {
            { 0, 1 }, { 0, 2 }, { 1, 3 }, { 2, 4 },
            { 5, 6 }, { 5, 7 }, { 7, 9 }, { 6, 8 }, { 8, 10 },
            { 5, 11 }, { 6, 12 }, { 11, 12 },
            { 11, 13 }, { 13, 15 }, { 12, 14 }, { 14, 16 }
        };

        private Panel titleBar;
        private Label titleLabel;
        private Button closeButton;
        private Button maximizeButton;
        private Button minimizeButton;
        private Button connectBtn;
        private Button startBtn;

        private IManagedCamera cam;
        private bool connected;
        private bool started;
        private bool capImg;
        private bool capImgComplete;

        private StereoCameraParameters stereoCameraParameters;
        private double fontSize;
        private int circleSize;
        private int fontThick;

        private readonly List<DistanceTrack> distanceTracks = new List<DistanceTrack>();
        private readonly List<PosePerson> cachedPosePeople = new List<PosePerson>();
        private int nextDistanceTrackId = 1;
        private int poseFrameCounter;

        public Form1()
        {
            if (!SetDllDirectory(@"..\Libraries"))
                throw new Win32Exception();

            ManagedSystem system = new ManagedSystem();
            ManagedCameraList camList = system.GetCameras();

            if (camList.Count == 0)
            {
                camList.Clear();
                system.Dispose();
                MessageBox.Show("No cameras Detected!");
                Environment.Exit(Environment.ExitCode);
            }

            cam = camList[0];
            stereoCameraParameters = new StereoCameraParameters();
            InitializeComponent();

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            Padding = new Padding(1);

            InitializeTitleBar();
            InitializeBody();

            Load += Form1_Load;
            SizeChanged += Form1_SizeChanged;
            pBoxLogo.Image = Bitmap.FromFile("APO_LOGO2.jpg");
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            UpdateWindowRegion();
            UpdateMaximizeButton();
        }

        private void InitializeTitleBar()
        {
            titleBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                BackColor = Color.FromArgb(33, 150, 243)
            };
            titleBar.MouseDown += TitleBar_MouseDown;
            titleBar.DoubleClick += (s, e) => ToggleMaximize();
            Controls.Add(titleBar);
            titleBar.BringToFront();

            titleLabel = new Label
            {
                Text = "Teledyne FLIR BumbleBee Demo",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                Location = new Point(10, 10),
                AutoSize = true
            };
            titleLabel.MouseDown += TitleBar_MouseDown;
            titleBar.Controls.Add(titleLabel);

            minimizeButton = CreateTitleButton("—");
            minimizeButton.Click += (s, e) => WindowState = FormWindowState.Minimized;
            titleBar.Controls.Add(minimizeButton);

            maximizeButton = CreateTitleButton("□");
            maximizeButton.Font = new Font("Segoe UI Symbol", 14);
            maximizeButton.Click += (s, e) => ToggleMaximize();
            titleBar.Controls.Add(maximizeButton);

            closeButton = CreateTitleButton("✕");
            closeButton.Click += (s, e) => Close();
            titleBar.Controls.Add(closeButton);
        }

        private Button CreateTitleButton(string text)
        {
            Button button = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Size = new Size(44, 40),
                Dock = DockStyle.Right,
                TabStop = false
            };

            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(55, 170, 255);
            return button;
        }

        private void InitializeBody()
        {
            connectBtn = new Button
            {
                Text = "連線",
                Size = new Size(120, 40),
                Location = new Point((ClientSize.Width - 300) / 2, 80),
                BackColor = Color.FromArgb(33, 150, 243),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            connectBtn.Click += connectBtn_Click;
            Controls.Add(connectBtn);
            connectBtn.BringToFront();

            startBtn = new Button
            {
                Text = "開始取像",
                Size = new Size(120, 40),
                Location = new Point(ClientSize.Width / 2, 80),
                BackColor = Color.FromArgb(33, 150, 243),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Enabled = false
            };
            startBtn.Click += startBtn_Click;
            Controls.Add(startBtn);
            startBtn.BringToFront();
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
        }

        private void Form1_SizeChanged(object sender, EventArgs e)
        {
            UpdateWindowRegion();
            UpdateMaximizeButton();
        }

        private void UpdateMaximizeButton()
        {
            if (maximizeButton != null)
                maximizeButton.Text = WindowState == FormWindowState.Maximized ? "❐" : "□";
        }

        private void UpdateWindowRegion()
        {
            if (WindowState == FormWindowState.Maximized)
            {
                Region = null;
                return;
            }

            if (Width > 0 && Height > 0)
                Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width, Height, 20, 20));
        }

        private void TitleBar_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || WindowState == FormWindowState.Maximized)
                return;

            ReleaseCapture();
            SendMessage(Handle, 0xA1, 0x2, 0);
        }

        private void GetStereoParameter(StereoCameraParameters p, INodeMap n)
        {
            p.coordinateOffset = (float)n.GetNode<IFloat>("Scan3dCoordinateOffset").Value;
            p.baseline = (float)n.GetNode<IFloat>("Scan3dBaseline").Value;
            p.focalLength = (float)n.GetNode<IFloat>("Scan3dFocalLength").Value;
            p.principalPointU = (float)n.GetNode<IFloat>("Scan3dPrincipalPointU").Value;
            p.principalPointV = (float)n.GetNode<IFloat>("Scan3dPrincipalPointV").Value;
            p.disparityScaleFactor = (float)n.GetNode<IFloat>("Scan3dCoordinateScale").Value;
            p.invalidDataFlag = n.GetNode<IBool>("Scan3dInvalidDataFlag").Value;
            p.invalidDataValue = (float)n.GetNode<IFloat>("Scan3dInvalidDataValue").Value;
        }

        private void SetBufferHandingMode(INodeMap n)
        {
            n.GetNode<IEnum>("StreamBufferHandlingMode").Value =
                StreamBufferHandlingModeEnum.NewestOnly.ToString();
        }

        private void connectBtn_Click(object sender, EventArgs e)
        {
            if (!connected)
            {
                cam.Init();
                INodeMap n = cam.GetNodeMap();

                for (int i = 0; i < 5; i++)
                    SetBufferHandingMode(cam.GetTLStreamNodeMap(i));

                ResoultionCheck(n);
                GetStereoParameter(stereoCameraParameters, n);
                connectBtn.Text = "斷線";
                startBtn.Enabled = true;
                connected = true;
            }
            else
            {
                cam.DeInit();
                connectBtn.Text = "連線";
                startBtn.Enabled = false;
                connected = false;
            }
        }

        private void startBtn_Click(object sender, EventArgs e)
        {
            if (!started)
            {
                distanceTracks.Clear();
                cachedPosePeople.Clear();
                nextDistanceTrackId = 1;
                poseFrameCounter = 0;
                capImg = true;
                capImgComplete = false;

                cam.BeginAcquisition();
                backgroundWorker1.RunWorkerAsync();

                startBtn.Text = "停止取像";
                connectBtn.Enabled = false;
                started = true;
            }
            else
            {
                capImg = false;
                startBtn.Enabled = false;
            }
        }

        private void ResoultionCheck(INodeMap n)
        {
            IEnum r = n.GetNode<IEnum>("StereoResolution");

            if (r.ToString() == "Full")
            {
                circleSize = 5;
                fontSize = 1;
                fontThick = 2;
            }
            else if (r.ToString() == "Quarter")
            {
                circleSize = 3;
                fontSize = .5;
                fontThick = 1;
            }
            else
            {
                circleSize = 4;
                fontSize = .8;
                fontThick = 2;
            }
        }

        private void backgroundWorker1_DoWork(object sender, DoWorkEventArgs e)
        {
            Net net = DnnInvoke.ReadNetFromONNX(PoseModelFile);
            net.SetPreferableBackend(Emgu.CV.Dnn.Backend.OpenCV);
            net.SetPreferableTarget(Target.Cpu);

            IManagedImageList imageList = new ManagedImageList();
            IManagedImage rectifiedImg = new ManagedImage();
            IManagedImage disparityImg = new ManagedImage();
            Stopwatch sw = new Stopwatch();

            try
            {
                while (capImg)
                {
                    sw.Restart();
                    imageList = cam.GetNextImageSync(3000);
                    sw.Stop();
                    SetCaptureMs(sw.Elapsed.TotalMilliseconds);

                    IManagedImage ro = imageList.GetByPayloadType(
                        ImagePayloadType.IMAGE_PAYLOAD_TYPE_RECTIFIED_SENSOR1);
                    IManagedImage d = imageList.GetByPayloadType(
                        ImagePayloadType.IMAGE_PAYLOAD_TYPE_DISPARITY_SENSOR1);

                    if (ro == null || d == null || ro.IsIncomplete || d.IsIncomplete)
                    {
                        imageList.Release();
                        continue;
                    }

                    sw.Restart();
                    rectifiedImg.DeepCopy(ro);
                    disparityImg.DeepCopy(d);
                    ro.Dispose();
                    d.Dispose();
                    imageList.Release();
                    sw.Stop();
                    SetCopyMs(sw.Elapsed.TotalMilliseconds);

                    int w = (int)rectifiedImg.Width;
                    int h = (int)rectifiedImg.Height;

                    using (Mat bgr = new Mat())
                    using (Mat rgb = new Mat(h, w, DepthType.Cv8U, 3, rectifiedImg.DataPtr, 0))
                    {
                        sw.Restart();
                        CvInvoke.CvtColor(rgb, bgr, ColorConversion.Rgb2Bgr);
                        sw.Stop();
                        SetConvertMs(sw.Elapsed.TotalMilliseconds);

                        unsafe
                        {
                            bool runInference = cachedPosePeople.Count == 0 ||
                                                (poseFrameCounter % PoseInferenceInterval) == 0;

                            if (runInference)
                                DetectPose(net, bgr);

                            sw.Restart();
                            DrawCachedPoseAndDistance(
                                (ushort*)disparityImg.NativeData,
                                bgr);
                            sw.Stop();
                            SetDistanceRenderMs(sw.Elapsed.TotalMilliseconds);
                        }

                        poseFrameCounter++;

                        sw.Restart();
                        QueueDisplayFrame(bgr);
                        sw.Stop();
                        SetBitmapQueueMs(sw.Elapsed.TotalMilliseconds);
                    }
                }
            }
            finally
            {
                net.Dispose();
                rectifiedImg.Dispose();
                disparityImg.Dispose();
                distanceTracks.Clear();
                cachedPosePeople.Clear();
                capImgComplete = true;

                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        cam.EndAcquisition();
                    }
                    catch
                    {
                    }

                    startBtn.Text = "開始取像";
                    startBtn.Enabled = true;
                    connectBtn.Enabled = true;
                    started = false;
                }));
            }
        }

        private Mat Letterbox(Mat src, out float scale, out int padX, out int padY)
        {
            scale = Math.Min(YoloSize / (float)src.Width, YoloSize / (float)src.Height);
            int nw = (int)Math.Round(src.Width * scale);
            int nh = (int)Math.Round(src.Height * scale);
            padX = (YoloSize - nw) / 2;
            padY = (YoloSize - nh) / 2;

            Mat dst = new Mat(YoloSize, YoloSize, DepthType.Cv8U, 3);
            dst.SetTo(new MCvScalar(114, 114, 114));

            using (Mat resized = new Mat())
            {
                CvInvoke.Resize(src, resized, new Size(nw, nh));
                using (Mat roi = new Mat(dst, new Rectangle(padX, padY, nw, nh)))
                    resized.CopyTo(roi);
            }

            return dst;
        }

        private unsafe void DetectPose(Net net, Mat mat)
        {
            float scale;
            int padX;
            int padY;

            using (Mat input = Letterbox(mat, out scale, out padX, out padY))
            using (Mat blob = DnnInvoke.BlobFromImage(
                input,
                1.0 / 255.0,
                new Size(YoloSize, YoloSize),
                new MCvScalar(),
                true,
                false))
            {
                net.SetInput(blob);

                Stopwatch forward = Stopwatch.StartNew();
                Mat output = net.Forward();
                forward.Stop();
                SetInferenceMs(forward.Elapsed.TotalMilliseconds);

                Stopwatch post = Stopwatch.StartNew();

                using (output)
                using (Mat reshaped = output.Reshape(1, PoseOutputChannels))
                using (Mat transposed = new Mat())
                {
                    CvInvoke.Transpose(reshaped, transposed);

                    List<PosePerson> people = new List<PosePerson>();
                    List<Rectangle> boxes = new List<Rectangle>();
                    List<float> scores = new List<float>();

                    float* data = (float*)transposed.DataPointer.ToPointer();
                    int rows = transposed.Rows;
                    int cols = transposed.Cols;

                    for (int i = 0; i < rows; i++, data += cols)
                    {
                        float score = data[4];
                        if (score < ConfidenceThreshold)
                            continue;

                        float cx = (data[0] - padX) / scale;
                        float cy = (data[1] - padY) / scale;
                        float bw = data[2] / scale;
                        float bh = data[3] / scale;

                        Rectangle box = ClampRect(
                            new Rectangle(
                                (int)(cx - bw / 2),
                                (int)(cy - bh / 2),
                                (int)bw,
                                (int)bh),
                            mat.Width,
                            mat.Height);

                        if (box.IsEmpty)
                            continue;

                        PosePerson person = new PosePerson
                        {
                            Box = box,
                            Keypoints = new PoseKeypoint[KeypointCount]
                        };

                        for (int k = 0; k < KeypointCount; k++)
                        {
                            int o = 5 + k * 3;
                            person.Keypoints[k] = new PoseKeypoint
                            {
                                X = (data[o] - padX) / scale,
                                Y = (data[o + 1] - padY) / scale,
                                Confidence = data[o + 2]
                            };
                        }

                        people.Add(person);
                        boxes.Add(box);
                        scores.Add(score);
                    }

                    cachedPosePeople.Clear();

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
                                cachedPosePeople.Add(people[index]);
                        }
                    }
                }

                post.Stop();
                SetPosePostMs(post.Elapsed.TotalMilliseconds);
            }
        }

        private unsafe void DrawCachedPoseAndDistance(ushort* disparityData, Mat mat)
        {
            AgeDistanceTracks();

            foreach (PosePerson person in cachedPosePeople)
            {
                DrawSkeleton(mat, person.Keypoints);

                Point center = GetTorsoCenter(person, mat.Width, mat.Height);
                double rawDistance = ComputeZvalue(
                    center,
                    person.Box,
                    disparityData,
                    mat.Width,
                    mat.Height);
                double displayDistance = UpdateSmoothedDistance(center, rawDistance);

                CvInvoke.Circle(
                    mat,
                    center,
                    circleSize + 1,
                    new MCvScalar(0, 255, 255),
                    -1);

                if (displayDistance > 0)
                {
                    CvInvoke.PutText(
                        mat,
                        displayDistance.ToString("F2") + "m",
                        new Point(center.X + 8, center.Y - 8),
                        FontFace.HersheyTriplex,
                        fontSize,
                        new MCvScalar(255, 255, 255),
                        fontThick);
                }
            }

            RemoveExpiredDistanceTracks();
        }

        private Point GetTorsoCenter(PosePerson person, int imgWidth, int imgHeight)
        {
            PoseKeypoint lsP = person.Keypoints[5];
            PoseKeypoint rsP = person.Keypoints[6];
            PoseKeypoint lhP = person.Keypoints[11];
            PoseKeypoint rhP = person.Keypoints[12];

            bool ls = IsValidKeypoint(lsP, imgWidth, imgHeight);
            bool rs = IsValidKeypoint(rsP, imgWidth, imgHeight);
            bool lh = IsValidKeypoint(lhP, imgWidth, imgHeight);
            bool rh = IsValidKeypoint(rhP, imgWidth, imgHeight);

            if (ls && rs && lh && rh)
            {
                return ClampPoint(
                    new Point(
                        (int)Math.Round((lsP.X + rsP.X + lhP.X + rhP.X) / 4.0),
                        (int)Math.Round((lsP.Y + rsP.Y + lhP.Y + rhP.Y) / 4.0)),
                    imgWidth,
                    imgHeight);
            }

            if (ls && rs)
            {
                return ClampPoint(
                    new Point(
                        (int)Math.Round((lsP.X + rsP.X) * .5f),
                        (int)Math.Round((lsP.Y + rsP.Y) * .5f + person.Box.Height * .18f)),
                    imgWidth,
                    imgHeight);
            }

            return ClampPoint(
                new Point(
                    person.Box.X + person.Box.Width / 2,
                    person.Box.Y + person.Box.Height / 2),
                imgWidth,
                imgHeight);
        }

        private Point ClampPoint(Point p, int w, int h)
        {
            return new Point(
                Math.Max(0, Math.Min(w - 1, p.X)),
                Math.Max(0, Math.Min(h - 1, p.Y)));
        }

        private void DrawSkeleton(Mat mat, PoseKeypoint[] keypoints)
        {
            for (int i = 0; i < SkeletonEdges.GetLength(0); i++)
            {
                PoseKeypoint a = keypoints[SkeletonEdges[i, 0]];
                PoseKeypoint b = keypoints[SkeletonEdges[i, 1]];

                if (IsValidKeypoint(a, mat.Width, mat.Height) &&
                    IsValidKeypoint(b, mat.Width, mat.Height))
                {
                    CvInvoke.Line(
                        mat,
                        new Point((int)a.X, (int)a.Y),
                        new Point((int)b.X, (int)b.Y),
                        new MCvScalar(0, 255, 0),
                        2);
                }
            }

            foreach (PoseKeypoint p in keypoints)
            {
                if (IsValidKeypoint(p, mat.Width, mat.Height))
                {
                    CvInvoke.Circle(
                        mat,
                        new Point((int)p.X, (int)p.Y),
                        circleSize,
                        new MCvScalar(0, 255, 255),
                        -1);
                }
            }
        }

        private bool IsValidKeypoint(PoseKeypoint p, int w, int h)
        {
            return p.Confidence >= KeypointThreshold &&
                   p.X >= 0 && p.X < w &&
                   p.Y >= 0 && p.Y < h;
        }

        private Rectangle ClampRect(Rectangle r, int width, int height)
        {
            int l = Math.Max(0, r.Left);
            int t = Math.Max(0, r.Top);
            int rr = Math.Min(width, r.Right);
            int bb = Math.Min(height, r.Bottom);

            return rr > l && bb > t
                ? Rectangle.FromLTRB(l, t, rr, bb)
                : Rectangle.Empty;
        }

        private unsafe double ComputeZvalue(
            Point center,
            Rectangle personRect,
            ushort* disparityData,
            int imgWidth,
            int imgHeight)
        {
            Rectangle person = ClampRect(personRect, imgWidth, imgHeight);
            if (person.IsEmpty)
                return 0;

            int rw = Math.Max(12, (int)(person.Width * .22));
            int rh = Math.Max(12, (int)(person.Height * .18));
            Rectangle roi = ClampRect(
                new Rectangle(center.X - rw / 2, center.Y - rh / 2, rw, rh),
                imgWidth,
                imgHeight);

            if (roi.IsEmpty)
                return 0;

            List<double> distances = new List<double>(
                Math.Max(32, roi.Width * roi.Height / 8));

            for (int y = roi.Top; y < roi.Bottom; y += 2)
            {
                for (int x = roi.Left; x < roi.Right; x += 2)
                {
                    ushort raw = disparityData[y * imgWidth + x];
                    if (raw == 0)
                        continue;

                    if (stereoCameraParameters.invalidDataFlag &&
                        Math.Abs(raw - stereoCameraParameters.invalidDataValue) < .5)
                        continue;

                    double disp = raw * stereoCameraParameters.disparityScaleFactor +
                                  stereoCameraParameters.coordinateOffset;
                    if (disp <= 0)
                        continue;

                    double distance = stereoCameraParameters.focalLength *
                                      stereoCameraParameters.baseline / disp;

                    if (distance > 0 && distance < 100000)
                        distances.Add(distance);
                }
            }

            return GetMedianInPlace(distances);
        }

        private double UpdateSmoothedDistance(Point center, double rawDistance)
        {
            DistanceTrack bestTrack = null;
            double bestDistanceSquared = DistanceTrackMatchPixels * DistanceTrackMatchPixels;

            foreach (DistanceTrack track in distanceTracks)
            {
                if (track.MatchedThisFrame)
                    continue;

                double dx = track.Center.X - center.X;
                double dy = track.Center.Y - center.Y;
                double ds = dx * dx + dy * dy;

                if (ds < bestDistanceSquared)
                {
                    bestDistanceSquared = ds;
                    bestTrack = track;
                }
            }

            if (bestTrack == null)
            {
                bestTrack = new DistanceTrack
                {
                    Id = nextDistanceTrackId++,
                    Center = center,
                    SmoothedDistance = rawDistance,
                    HasDistance = rawDistance > 0,
                    MissedFrames = 0,
                    MatchedThisFrame = true
                };

                distanceTracks.Add(bestTrack);
                return bestTrack.HasDistance ? bestTrack.SmoothedDistance : 0;
            }

            bestTrack.Center = center;
            bestTrack.MissedFrames = 0;
            bestTrack.MatchedThisFrame = true;

            if (rawDistance <= 0)
                return bestTrack.HasDistance ? bestTrack.SmoothedDistance : 0;

            if (!bestTrack.HasDistance)
            {
                bestTrack.SmoothedDistance = rawDistance;
                bestTrack.HasDistance = true;
                return bestTrack.SmoothedDistance;
            }

            double difference = Math.Abs(rawDistance - bestTrack.SmoothedDistance);
            double alpha = difference > DistanceJumpThresholdMeters
                ? .55
                : DistanceEmaAlpha;

            bestTrack.SmoothedDistance =
                bestTrack.SmoothedDistance * (1 - alpha) + rawDistance * alpha;

            return bestTrack.SmoothedDistance;
        }

        private void AgeDistanceTracks()
        {
            foreach (DistanceTrack track in distanceTracks)
            {
                track.MatchedThisFrame = false;
                track.MissedFrames++;
            }
        }

        private void RemoveExpiredDistanceTracks()
        {
            distanceTracks.RemoveAll(
                track => track.MissedFrames > DistanceTrackMaxMissedFrames);
        }

        private double GetMedianInPlace(List<double> values)
        {
            if (values == null || values.Count == 0)
                return 0;

            values.Sort();
            int m = values.Count / 2;

            return values.Count % 2 == 0
                ? (values[m - 1] + values[m]) * .5
                : values[m];
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            capImg = false;
            DisposePerformanceOverlay();
            DisposeFastDisplay();

            try
            {
                if (started)
                    cam.EndAcquisition();
            }
            catch
            {
            }

            try
            {
                if (connected)
                    cam.DeInit();
            }
            catch
            {
            }

            cam?.Dispose();
        }

        private void backgroundWorker1_RunWorkerCompleted(
            object sender,
            RunWorkerCompletedEventArgs e)
        {
        }
    }

    public class StereoCameraParameters
    {
        public float coordinateOffset;
        public float baseline;
        public float focalLength;
        public float principalPointU;
        public float principalPointV;
        public float disparityScaleFactor;
        public bool invalidDataFlag;
        public float invalidDataValue;
    }

    internal struct PoseKeypoint
    {
        public float X;
        public float Y;
        public float Confidence;
    }

    internal class PosePerson
    {
        public Rectangle Box;
        public PoseKeypoint[] Keypoints;
    }

    internal class DistanceTrack
    {
        public int Id;
        public Point Center;
        public double SmoothedDistance;
        public bool HasDistance;
        public int MissedFrames;
        public bool MatchedThisFrame;
    }
}
