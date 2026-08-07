using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
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
        private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

        private const int YoloSize = 640;
        private const float ConfidenceThreshold = 0.50f;
        private const float NmsThreshold = 0.45f;

        private Panel titleBar;
        private Label titleLabel;
        private Button closeButton;
        private Button connectBtn;
        private Button startBtn;
        private Button minimizeButton;

        private IManagedCamera cam;
        private bool connected;
        private bool started;
        private bool capImg;
        private bool capImgComplete;
        private StereoCameraParameters stereoCameraParameters;
        private double fontSize;
        private int circleSize;
        private int fontThick;

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

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(400, 300);
            BackColor = Color.White;
            Padding = new Padding(1);

            Load += (s, e) =>
            {
                Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width, Height, 20, 20));
            };

            InitializeTitleBar();
            InitializeBody();
            InitializeComponent();
            pBoxLogo.Image = Bitmap.FromFile("APO_LOGO2.jpg");
        }

        private void InitializeTitleBar()
        {
            titleBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(33, 150, 243) };
            titleBar.MouseDown += TitleBar_MouseDown;
            Controls.Add(titleBar);

            titleLabel = new Label { Text = "Teledyne FLIR BumbleBee Demo", ForeColor = Color.White, Font = new Font("Segoe UI", 12, FontStyle.Bold), Location = new Point(10, 10), AutoSize = true };
            titleBar.Controls.Add(titleLabel);

            closeButton = new Button { Text = "✕", FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.Transparent, Size = new Size(40, 40), Dock = DockStyle.Right };
            closeButton.FlatAppearance.BorderSize = 0;
            closeButton.Click += (s, e) => Close();
            titleBar.Controls.Add(closeButton);

            minimizeButton = new Button { Text = "—", FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.Transparent, Size = new Size(40, 40), Dock = DockStyle.Right };
            minimizeButton.FlatAppearance.BorderSize = 0;
            minimizeButton.Click += (s, e) => WindowState = FormWindowState.Minimized;
            titleBar.Controls.Add(minimizeButton);
        }

        private void InitializeBody()
        {
            connectBtn = new Button { Text = "連線", Size = new Size(120, 40), Location = new Point((ClientSize.Width - 300) / 2, 80), BackColor = Color.FromArgb(33, 150, 243), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            connectBtn.Click += connectBtn_Click;
            Controls.Add(connectBtn);

            startBtn = new Button { Text = "開始取像", Size = new Size(120, 40), Location = new Point(ClientSize.Width / 2, 80), BackColor = Color.FromArgb(33, 150, 243), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Enabled = false };
            startBtn.Click += startBtn_Click;
            Controls.Add(startBtn);
        }

        private void TitleBar_MouseDown(object sender, MouseEventArgs e)
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 0x2, 0);
        }

        private void GetStereoParameter(StereoCameraParameters parameters, INodeMap nodeMap)
        {
            parameters.coordinateOffset = (float)nodeMap.GetNode<IFloat>("Scan3dCoordinateOffset").Value;
            parameters.baseline = (float)nodeMap.GetNode<IFloat>("Scan3dBaseline").Value;
            parameters.focalLength = (float)nodeMap.GetNode<IFloat>("Scan3dFocalLength").Value;
            parameters.principalPointU = (float)nodeMap.GetNode<IFloat>("Scan3dPrincipalPointU").Value;
            parameters.principalPointV = (float)nodeMap.GetNode<IFloat>("Scan3dPrincipalPointV").Value;
            parameters.disparityScaleFactor = (float)nodeMap.GetNode<IFloat>("Scan3dCoordinateScale").Value;
            parameters.invalidDataFlag = nodeMap.GetNode<IBool>("Scan3dInvalidDataFlag").Value;
            parameters.invalidDataValue = (float)nodeMap.GetNode<IFloat>("Scan3dInvalidDataValue").Value;
        }

        private void SetBufferHandingMode(INodeMap nodeMap)
        {
            nodeMap.GetNode<IEnum>("StreamBufferHandlingMode").Value = StreamBufferHandlingModeEnum.NewestOnly.ToString();
        }

        private void connectBtn_Click(object sender, EventArgs e)
        {
            if (!connected)
            {
                cam.Init();
                INodeMap nodeMap = cam.GetNodeMap();
                for (int i = 0; i < 5; i++)
                    SetBufferHandingMode(cam.GetTLStreamNodeMap(i));

                ResoultionCheck(nodeMap);
                GetStereoParameter(stereoCameraParameters, nodeMap);
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

        private void ResoultionCheck(INodeMap nodeMap)
        {
            IEnum resolution = nodeMap.GetNode<IEnum>("StereoResolution");
            if (resolution.ToString() == "Full")
            {
                circleSize = 5;
                fontSize = 1.0;
                fontThick = 2;
            }
            else if (resolution.ToString() == "Quarter")
            {
                circleSize = 3;
                fontSize = 0.5;
                fontThick = 1;
            }
            else
            {
                circleSize = 4;
                fontSize = 0.8;
                fontThick = 2;
            }
        }

        public delegate void InvokeDelegate(Bitmap bmp, int imgWidth, int imgHeight);

        private void backgroundWorker1_DoWork(object sender, DoWorkEventArgs e)
        {
            Net net = DnnInvoke.ReadNetFromONNX("yolov8n.onnx");
            net.SetPreferableBackend(Emgu.CV.Dnn.Backend.OpenCV);
            net.SetPreferableTarget(Target.Cpu);

            IManagedImageList imageList = new ManagedImageList();
            Mat disparityMat8Bit = new Mat();
            IManagedImage rectifiedImg = new ManagedImage();
            IManagedImage disparityImg = new ManagedImage();

            try
            {
                while (capImg)
                {
                    imageList = cam.GetNextImageSync(3000);
                    IManagedImage rectifiedOrigin = imageList.GetByPayloadType(ImagePayloadType.IMAGE_PAYLOAD_TYPE_RECTIFIED_SENSOR1);
                    IManagedImage disparityOrigin = imageList.GetByPayloadType(ImagePayloadType.IMAGE_PAYLOAD_TYPE_DISPARITY_SENSOR1);

                    if (rectifiedOrigin == null || disparityOrigin == null || rectifiedOrigin.IsIncomplete || disparityOrigin.IsIncomplete)
                    {
                        imageList.Release();
                        continue;
                    }

                    rectifiedImg.DeepCopy(rectifiedOrigin);
                    disparityImg.DeepCopy(disparityOrigin);
                    rectifiedOrigin.Dispose();
                    disparityOrigin.Dispose();
                    imageList.Release();

                    int imgWidth = (int)rectifiedImg.Width;
                    int imgHeight = (int)rectifiedImg.Height;
                    int imgStride = (int)rectifiedImg.Stride;

                    using (Mat bgrMat = new Mat())
                    using (Mat rgbMat = new Mat(imgHeight, imgWidth, DepthType.Cv8U, 3, rectifiedImg.DataPtr, 0))
                    {
                        CvInvoke.CvtColor(rgbMat, bgrMat, ColorConversion.Rgb2Bgr);

                        unsafe
                        {
                            PersonDetect(net, (ushort*)disparityImg.NativeData, bgrMat);
                        }

                        using (Bitmap bmp = new Bitmap(imgWidth, imgHeight, imgStride, PixelFormat.Format24bppRgb, bgrMat.DataPointer))
                        {
                            pBox.BeginInvoke(new InvokeDelegate(InvokeDisplay), new Bitmap(bmp), imgWidth, imgHeight);
                        }
                    }
                }
            }
            finally
            {
                net.Dispose();
                disparityMat8Bit.Dispose();
                rectifiedImg.Dispose();
                disparityImg.Dispose();
                capImgComplete = true;

                BeginInvoke(new Action(() =>
                {
                    try { cam.EndAcquisition(); } catch { }
                    startBtn.Text = "開始取像";
                    startBtn.Enabled = true;
                    connectBtn.Enabled = true;
                    started = false;
                }));
            }
        }

        private Mat Letterbox(Mat source, out float scale, out int padX, out int padY)
        {
            scale = Math.Min(YoloSize / (float)source.Width, YoloSize / (float)source.Height);
            int resizedWidth = (int)Math.Round(source.Width * scale);
            int resizedHeight = (int)Math.Round(source.Height * scale);
            padX = (YoloSize - resizedWidth) / 2;
            padY = (YoloSize - resizedHeight) / 2;

            Mat destination = new Mat(YoloSize, YoloSize, DepthType.Cv8U, 3);
            destination.SetTo(new MCvScalar(114, 114, 114));

            using (Mat resized = new Mat())
            {
                CvInvoke.Resize(source, resized, new Size(resizedWidth, resizedHeight));
                using (Mat roi = new Mat(destination, new Rectangle(padX, padY, resizedWidth, resizedHeight)))
                {
                    resized.CopyTo(roi);
                }
            }
            return destination;
        }

        unsafe private void PersonDetect(Net net, ushort* disparityData, Mat mat)
        {
            float scale;
            int padX;
            int padY;

            using (Mat input = Letterbox(mat, out scale, out padX, out padY))
            using (Mat blob = DnnInvoke.BlobFromImage(input, 1.0 / 255.0, new Size(YoloSize, YoloSize), new MCvScalar(), true, false))
            {
                net.SetInput(blob);
                using (Mat output = net.Forward())
                using (Mat reshaped = output.Reshape(1, 84))
                using (Mat transposed = new Mat())
                {
                    CvInvoke.Transpose(reshaped, transposed);
                    List<Rectangle> boxes = new List<Rectangle>();
                    List<float> scores = new List<float>();
                    float* data = (float*)transposed.DataPointer.ToPointer();
                    int rows = transposed.Rows;
                    int columns = transposed.Cols;

                    for (int i = 0; i < rows; i++, data += columns)
                    {
                        float personScore = data[4];
                        if (personScore < ConfidenceThreshold)
                            continue;

                        float centerX = (data[0] - padX) / scale;
                        float centerY = (data[1] - padY) / scale;
                        float width = data[2] / scale;
                        float height = data[3] / scale;
                        Rectangle box = ClampRect(new Rectangle((int)(centerX - width / 2.0f), (int)(centerY - height / 2.0f), (int)width, (int)height), mat.Width, mat.Height);

                        if (box.Width > 2 && box.Height > 2)
                        {
                            boxes.Add(box);
                            scores.Add(personScore);
                        }
                    }

                    if (boxes.Count == 0)
                        return;

                    using (VectorOfRect boxVector = new VectorOfRect(boxes.ToArray()))
                    using (VectorOfFloat scoreVector = new VectorOfFloat(scores.ToArray()))
                    using (VectorOfInt indices = new VectorOfInt())
                    {
                        DnnInvoke.NMSBoxes(boxVector, scoreVector, ConfidenceThreshold, NmsThreshold, indices);
                        foreach (int index in indices.ToArray())
                        {
                            Rectangle rect = boxes[index];
                            double distance = ComputeZvalue(rect, disparityData, mat.Width, mat.Height);
                            Point center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

                            CvInvoke.Rectangle(mat, rect, new MCvScalar(0, 255, 0), 2);
                            CvInvoke.Circle(mat, center, circleSize, new MCvScalar(255, 0, 0), -1);
                            if (distance > 0)
                            {
                                CvInvoke.PutText(mat, distance.ToString("F2") + "m", center, FontFace.HersheyTriplex, fontSize, new Bgr(255, 255, 255).MCvScalar, fontThick);
                            }
                        }
                    }
                }
            }
        }

        private Rectangle ClampRect(Rectangle rect, int width, int height)
        {
            int left = Math.Max(0, rect.Left);
            int top = Math.Max(0, rect.Top);
            int right = Math.Min(width, rect.Right);
            int bottom = Math.Min(height, rect.Bottom);
            if (right <= left || bottom <= top)
                return Rectangle.Empty;
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        unsafe private double ComputeZvalue(Rectangle personRect, ushort* disparityData, int imgWidth, int imgHeight)
        {
            Rectangle person = ClampRect(personRect, imgWidth, imgHeight);
            if (person.IsEmpty)
                return 0;

            int roiWidth = Math.Max(1, (int)(person.Width * 0.45));
            int roiHeight = Math.Max(1, (int)(person.Height * 0.45));
            int roiLeft = person.X + (person.Width - roiWidth) / 2;
            int roiTop = person.Y + (int)(person.Height * 0.25);
            Rectangle roi = ClampRect(new Rectangle(roiLeft, roiTop, roiWidth, roiHeight), imgWidth, imgHeight);
            List<double> distances = new List<double>(Math.Max(32, roi.Width * roi.Height / 16));
            const int sampleStep = 2;

            for (int y = roi.Top; y < roi.Bottom; y += sampleStep)
            {
                for (int x = roi.Left; x < roi.Right; x += sampleStep)
                {
                    ushort rawDisparity = disparityData[y * imgWidth + x];
                    if (rawDisparity == 0)
                        continue;
                    if (stereoCameraParameters.invalidDataFlag && Math.Abs(rawDisparity - stereoCameraParameters.invalidDataValue) < 0.5)
                        continue;

                    double actualDisparity = rawDisparity * stereoCameraParameters.disparityScaleFactor + stereoCameraParameters.coordinateOffset;
                    if (actualDisparity <= 0)
                        continue;

                    double distance = stereoCameraParameters.focalLength * stereoCameraParameters.baseline / actualDisparity;
                    if (distance > 0 && distance < 100000)
                        distances.Add(distance);
                }
            }
            return GetMedianInPlace(distances);
        }

        private double GetMedianInPlace(List<double> values)
        {
            if (values == null || values.Count == 0)
                return 0;
            values.Sort();
            int middle = values.Count / 2;
            return values.Count % 2 == 0 ? (values[middle - 1] + values[middle]) * 0.5 : values[middle];
        }

        private void InvokeDisplay(Bitmap bmp, int imgWidth, int imgHeight)
        {
            Bitmap oldBitmap = pBox.Image as Bitmap;
            pBox.Image = bmp;
            pBox.Width = imgWidth;
            pBox.Height = imgHeight;
            oldBitmap?.Dispose();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            capImg = false;
            try { if (started) cam.EndAcquisition(); } catch { }
            try { if (connected) cam.DeInit(); } catch { }
            cam?.Dispose();
        }

        private void backgroundWorker1_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
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
}
