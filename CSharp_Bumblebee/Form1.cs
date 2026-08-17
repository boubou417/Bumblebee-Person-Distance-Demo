using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Emgu.CV;
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

        private const float ConfidenceThreshold = 0.50f;
        private const float NmsThreshold = 0.45f;
        private const int DetectionSourceInterval = 2;

        private const double DistanceEmaAlpha = 0.25;
        private const double DistanceJumpThresholdMeters = 0.75;
        private const int DistanceTrackMaxMissedFrames = 8;
        private const int DistanceTrackMatchPixels = 140;

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
        private readonly List<PersonDetection> cachedDetections = new List<PersonDetection>();
        private int nextDistanceTrackId = 1;
        private int cameraFrameCounter;

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
            FormClosing += Form1_FormClosing;
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
                nextDistanceTrackId = 1;
                cameraFrameCounter = 0;
                capImg = true;
                capImgComplete = false;

                if (!StartDetectionWorker())
                {
                    capImg = false;
                    capImgComplete = true;
                    return;
                }

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

        private Rectangle ClampRect(Rectangle r, int width, int height)
        {
            int left = Math.Max(0, r.Left);
            int top = Math.Max(0, r.Top);
            int right = Math.Min(width, r.Right);
            int bottom = Math.Min(height, r.Bottom);

            return right > left && bottom > top
                ? Rectangle.FromLTRB(left, top, right, bottom)
                : Rectangle.Empty;
        }

        private Point GetDetectionCenter(Rectangle box, int imageWidth, int imageHeight)
        {
            Rectangle clipped = ClampRect(box, imageWidth, imageHeight);
            if (clipped.IsEmpty)
                return Point.Empty;

            // Chest/body-center sample point. It stays away from the floor/background
            // and is more useful for disparity distance than the full box bottom half.
            Point center = new Point(
                clipped.X + clipped.Width / 2,
                clipped.Y + (int)Math.Round(clipped.Height * 0.50));

            return new Point(
                Math.Max(0, Math.Min(imageWidth - 1, center.X)),
                Math.Max(0, Math.Min(imageHeight - 1, center.Y)));
        }

        private unsafe void DrawCachedDetectionAndDistance(
            ushort* disparityData,
            Mat mat)
        {
            AgeDistanceTracks();
            List<PersonDetection> detections = GetDetectionSnapshot();

            foreach (PersonDetection detection in detections)
            {
                Point center = GetDetectionCenter(
                    detection.Box,
                    mat.Width,
                    mat.Height);

                if (center == Point.Empty)
                    continue;

                double rawDistance = ComputeZvalue(
                    center,
                    detection.Box,
                    disparityData,
                    mat.Width,
                    mat.Height);

                UpdateSmoothedDistance(center, rawDistance);
            }

            RemoveExpiredDistanceTracks();
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

            int roiWidth = Math.Max(12, (int)(person.Width * .22));
            int roiHeight = Math.Max(12, (int)(person.Height * .18));
            Rectangle roi = ClampRect(
                new Rectangle(
                    center.X - roiWidth / 2,
                    center.Y - roiHeight / 2,
                    roiWidth,
                    roiHeight),
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
                    {
                        continue;
                    }

                    double disparity =
                        raw * stereoCameraParameters.disparityScaleFactor +
                        stereoCameraParameters.coordinateOffset;

                    if (disparity <= 0)
                        continue;

                    double distance =
                        stereoCameraParameters.focalLength *
                        stereoCameraParameters.baseline /
                        disparity;

                    if (distance > 0 && distance < 100000)
                        distances.Add(distance);
                }
            }

            return GetMedianInPlace(distances);
        }

        private double UpdateSmoothedDistance(Point center, double rawDistance)
        {
            DistanceTrack bestTrack = null;
            double bestDistanceSquared =
                DistanceTrackMatchPixels * DistanceTrackMatchPixels;

            foreach (DistanceTrack track in distanceTracks)
            {
                if (track.MatchedThisFrame)
                    continue;

                double dx = track.Center.X - center.X;
                double dy = track.Center.Y - center.Y;
                double distanceSquared = dx * dx + dy * dy;

                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
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
                bestTrack.SmoothedDistance * (1 - alpha) +
                rawDistance * alpha;

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
            int middle = values.Count / 2;

            return values.Count % 2 == 0
                ? (values[middle - 1] + values[middle]) * .5
                : values[middle];
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            capImg = false;
            StopDetectionWorker();
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

    internal sealed class PersonDetection
    {
        public Rectangle Box;
        public float Confidence;
    }

    internal sealed class DistanceTrack
    {
        public int Id;
        public Point Center;
        public double SmoothedDistance;
        public bool HasDistance;
        public int MissedFrames;
        public bool MatchedThisFrame;
    }
}
