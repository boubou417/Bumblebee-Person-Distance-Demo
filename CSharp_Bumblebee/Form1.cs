using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Dnn;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using SpinnakerNET;
using SpinnakerNET.GenApi;
using System.Drawing.Drawing2D;

namespace CSharp_Bumblebee
{
    public partial class Form1 : Form
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetDllDirectory(string dllPathName);
        private Panel titleBar;
        private Label titleLabel;
        private Button closeButton;
        private Button connectBtn;
        private Button startBtn;
        private Button minimizeButton;
        private Button maximizeButton;
        private bool isMaximized = false;
        private Rectangle normalBounds;

        IManagedCamera cam;
        bool connected = false;
        bool started = false;
        bool capImg;
        bool capImgComplete;
        StereoCameraParameters stereoCameraParameters;
        List<ColorClass> colorList;
        List<byte[]> colorArr;
        double fontSize;
        int circleSize;
        int fontThick;

        class ColorClass
        {
            public byte B;
            public byte G;
            public byte R;
        }

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        private void TitleBar_MouseDown(object sender, MouseEventArgs e)
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 0x2, 0);
        }

        [DllImport("Gdi32.dll", EntryPoint = "CreateRoundRectRgn")]
        private static extern IntPtr CreateRoundRectRgn(int nLeft, int nTop, int nRight, int nBottom, int nWidthEllipse, int nHeightEllipse);

        public Form1()
        {
            if (!SetDllDirectory(@"..\Libraries")) throw new Win32Exception();
            ManagedSystem system = new ManagedSystem();
            ManagedCameraList camList = system.GetCameras();
            if (camList.Count == 0)
            {
                camList.Clear(); system.Dispose(); MessageBox.Show("No cameras Detected!"); Environment.Exit(Environment.ExitCode);
            }
            else cam = camList[0];
            stereoCameraParameters = new StereoCameraParameters();
            colorList = new List<ColorClass>(); colorArr = new List<byte[]>();
            for (int i = 0; i < ushort.MaxValue; i++) { colorArr.Add(new byte[3]); colorList.Add(new ColorClass()); }
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen; Size = new Size(400, 300); BackColor = Color.White; Padding = new Padding(1);
            Load += (s, e) => { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width, Height, 20, 20)); };
            InitializeTitleBar(); InitializeBody(); InitializeComponent();
            string logoPath = "APO_LOGO2.jpg"; Image logoImg = Bitmap.FromFile(logoPath); pBoxLogo.Image = logoImg;
        }

        private void InitializeTitleBar()
        {
            titleBar = new Panel() { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(33, 150, 243) }; titleBar.MouseDown += TitleBar_MouseDown; Controls.Add(titleBar);
            titleLabel = new Label() { Text = "Teledyne FLIR BumbleBee Demo", ForeColor = Color.White, Font = new Font("Segoe UI", 12, FontStyle.Bold), Location = new Point(10, 10), AutoSize = true }; titleBar.Controls.Add(titleLabel);
            closeButton = new Button() { Text = "✕", FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.Transparent, Font = new Font("Segoe UI", 10), Size = new Size(40, 40), Dock = DockStyle.Right }; closeButton.FlatAppearance.BorderSize = 0; closeButton.Click += (s, e) => Close(); titleBar.Controls.Add(closeButton);
            minimizeButton = new Button() { Text = "—", FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.Transparent, Font = new Font("Segoe UI", 10), Size = new Size(40, 40), Dock = DockStyle.Right }; minimizeButton.FlatAppearance.BorderSize = 0; minimizeButton.Click += (s, e) => WindowState = FormWindowState.Minimized; titleBar.Controls.Add(minimizeButton);
            titleBar.Controls.SetChildIndex(minimizeButton, 0); titleBar.Controls.SetChildIndex(closeButton, 2);
        }

        private void InitializeBody()
        {
            connectBtn = new Button() { Text = "連線", Size = new Size(120, 40), Location = new Point((ClientSize.Width - 300) / 2, 80), BackColor = Color.FromArgb(33, 150, 243), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("中國龍特圓體", 14, FontStyle.Regular) };
            connectBtn.FlatAppearance.BorderSize = 0; connectBtn.MouseEnter += (s, e) => connectBtn.BackColor = Color.FromArgb(50, 160, 130); connectBtn.MouseLeave += (s, e) => connectBtn.BackColor = Color.FromArgb(33, 150, 243); MakeButtonRound(connectBtn, 10); connectBtn.Click += connectBtn_Click; Controls.Add(connectBtn);
            startBtn = new Button() { Text = "開始取像", Size = new Size(120, 40), Location = new Point(ClientSize.Width / 2, 80), BackColor = Color.FromArgb(33, 150, 243), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("中國龍特圓體", 14, FontStyle.Regular) };
            startBtn.FlatAppearance.BorderSize = 0; startBtn.MouseEnter += (s, e) => startBtn.BackColor = Color.FromArgb(50, 160, 130); startBtn.MouseLeave += (s, e) => startBtn.BackColor = Color.FromArgb(33, 150, 243); MakeButtonRound(startBtn, 10); startBtn.Click += startBtn_Click; startBtn.Enabled = false; Controls.Add(startBtn);
        }

        private void MakeButtonRound(Button btn, int radius)
        {
            Rectangle bounds = btn.ClientRectangle; GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, radius, radius, 180, 90); path.AddArc(bounds.Right - radius, bounds.Y, radius, radius, 270, 90); path.AddArc(bounds.Right - radius, bounds.Bottom - radius, radius, radius, 0, 90); path.AddArc(bounds.X, bounds.Bottom - radius, radius, radius, 90, 90); path.CloseAllFigures(); btn.Region = new Region(path);
        }

        private void GetStereoParameter(StereoCameraParameters p, INodeMap nodeMap)
        {
            p.coordinateOffset = (float)nodeMap.GetNode<IFloat>("Scan3dCoordinateOffset").Value; p.baseline = (float)nodeMap.GetNode<IFloat>("Scan3dBaseline").Value; p.focalLength = (float)nodeMap.GetNode<IFloat>("Scan3dFocalLength").Value; p.principalPointU = (float)nodeMap.GetNode<IFloat>("Scan3dPrincipalPointU").Value; p.principalPointV = (float)nodeMap.GetNode<IFloat>("Scan3dPrincipalPointV").Value; p.disparityScaleFactor = (float)nodeMap.GetNode<IFloat>("Scan3dCoordinateScale").Value; p.invalidDataFlag = nodeMap.GetNode<IBool>("Scan3dInvalidDataFlag").Value; p.invalidDataValue = (float)nodeMap.GetNode<IFloat>("Scan3dInvalidDataValue").Value;
        }
        private void SetBufferHandingMode(INodeMap nodeMap) { nodeMap.GetNode<IEnum>("StreamBufferHandlingMode").Value = StreamBufferHandlingModeEnum.NewestOnly.ToString(); }
        private void connectBtn_Click(object sender, EventArgs e)
        {
            if (!connected) { cam.Init(); INodeMap nodeMap = cam.GetNodeMap(); for (int i = 0; i < 5; i++) SetBufferHandingMode(cam.GetTLStreamNodeMap(i)); ResoultionCheck(nodeMap); GetStereoParameter(stereoCameraParameters, nodeMap); connectBtn.Text = "斷線"; startBtn.Enabled = true; connected = true; }
            else { cam.DeInit(); connectBtn.Text = "連線"; startBtn.Enabled = false; connected = false; }
        }
        private void startBtn_Click(object sender, EventArgs e)
        {
            if (!started) { capImg = true; capImgComplete = false; cam.BeginAcquisition(); backgroundWorker1.RunWorkerAsync(); startBtn.Text = "停止取像"; connectBtn.Enabled = false; started = true; }
            else { capImg = false; while (!capImgComplete) System.Threading.Thread.Sleep(1000); cam.EndAcquisition(); startBtn.Text = "開始取像"; connectBtn.Enabled = true; started = false; }
        }
        private void ResoultionCheck(INodeMap nodeMap)
        {
            IEnum r = nodeMap.GetNode<IEnum>("StereoResolution"); if (r.ToString() == "Full") { circleSize = 5; fontSize = 1; fontThick = 2; } else if (r.ToString() == "Quarter") { circleSize = 3; fontSize = 0.5; fontThick = 1; } else { circleSize = 4; fontSize = 0.8; fontThick = 2; }
        }
        public delegate void InvokeDelegate(Bitmap bmp, int imgWidth, int imgHeight);
        public delegate void InvokeExceptionDelegate();
        private void backgroundWorker1_DoWork(object sender, DoWorkEventArgs e)
        {
            string modelPath = "yolov8n.onnx"; string[] classNames = System.IO.File.ReadAllLines("coco.names"); Net net = DnnInvoke.ReadNetFromONNX(modelPath); net.SetPreferableBackend(Backend.OpenCV); net.SetPreferableTarget(Target.Cuda);
            IManagedImageList imageList = new ManagedImageList(); float minDepthVal = 0f, maxDepthVal = 0f; Mat disparityMat8Bit = new Mat(); IManagedImage rectifiedImg = new ManagedImage(); IManagedImage disparityImg = new ManagedImage(); IManagedImage depthImg = new ManagedImage(); IManagedImage heatMapImg = new ManagedImage();
            while (capImg)
            {
                imageList = cam.GetNextImageSync(3000); IManagedImage rectifiedImgOrigin = imageList.GetByPayloadType(ImagePayloadType.IMAGE_PAYLOAD_TYPE_RECTIFIED_SENSOR1); IManagedImage disparityImgOrigin = imageList.GetByPayloadType(ImagePayloadType.IMAGE_PAYLOAD_TYPE_DISPARITY_SENSOR1);
                if (rectifiedImgOrigin == null || disparityImgOrigin == null || rectifiedImgOrigin.IsIncomplete || disparityImgOrigin.IsIncomplete) { imageList.Release(); Console.WriteLine("Image Release"); continue; }
                rectifiedImg.DeepCopy(rectifiedImgOrigin); disparityImg.DeepCopy(disparityImgOrigin); rectifiedImgOrigin.Dispose(); disparityImgOrigin.Dispose(); imageList.Release(); int imgWidth = (int)rectifiedImg.Width; int imgHeight = (int)rectifiedImg.Height; int imgStride = (int)rectifiedImg.Stride;
                if (rectifiedImg != null && disparityImg != null)
                {
                    Mat bgrMat = new Mat(); Mat rgbMat = new Mat((int)rectifiedImg.Height, (int)rectifiedImg.Width, DepthType.Cv8U, 3, rectifiedImg.DataPtr, 0); CvInvoke.CvtColor(rgbMat, bgrMat, ColorConversion.Rgb2Bgr); Mat disparityMat = null;
                    if (cbDisparity.Checked)
                    {
                        unsafe { depthImg = ManagedImageUtilityStereo.CreateDepthImage(disparityImg, stereoCameraParameters, 0, &minDepthVal, &maxDepthVal); heatMapImg = ManagedImageUtilityHeatmap.CreateHeatmap(depthImg, 0f, 23000f, HeatmapColor.HEATMAP_BLUE, HeatmapColor.HEATMAP_WHITE, true, 0); }
                        disparityMat = new Mat((int)heatMapImg.Height, (int)heatMapImg.Width, DepthType.Cv16U, 3, heatMapImg.DataPtr, 0); CvInvoke.Normalize(disparityMat, disparityMat8Bit, 0, 255, NormType.MinMax, DepthType.Cv8U);
                    }
                    unsafe { PersionDetect(net, (ushort*)disparityImg.NativeData, ref bgrMat, ref disparityMat8Bit); }
                    IntPtr matPtr = (!cbDisparity.Checked || disparityMat == null) ? bgrMat.DataPointer : disparityMat8Bit.DataPointer; Bitmap bmp = new Bitmap(imgWidth, imgHeight, imgStride, PixelFormat.Format24bppRgb, matPtr); pBox.BeginInvoke(new InvokeDelegate(InvokeDisplay), new Bitmap(bmp), imgWidth, imgHeight);
                }
            }
            capImgComplete = true;
        }

        unsafe private void PersionDetect(Net net, ushort* disparityData, ref Mat mat, ref Mat disparityMat)
        {
            Mat resized = new Mat(); CvInvoke.Resize(mat, resized, new Size(640, 640)); Mat blob = DnnInvoke.BlobFromImage(resized, 1 / 255.0, new Size(640, 640), new MCvScalar(), swapRB: true, crop: false); net.SetInput(blob); Mat output = net.Forward(); float confidenceThreshold = 0.5f; float nmsThreshold = 0.45f; Mat reShapeMat = output.Reshape(1, 84); Mat transportMat = new Mat(); CvInvoke.Transpose(reShapeMat, transportMat); List<Rectangle> boxes = new List<Rectangle>(); List<float> confidences = new List<float>(); IntPtr dataPtr = transportMat.GetDataPointer(0);
            float* floatPtr = (float*)dataPtr.ToPointer(); int sizeDimension0 = reShapeMat.SizeOfDimension[0]; int sizeDimension1 = reShapeMat.SizeOfDimension[1];
            for (int i = 0; i < sizeDimension1; i++) { if (floatPtr[4] > confidenceThreshold) { confidences.Add(floatPtr[4]); float cx = floatPtr[0] * mat.Width / 640; float cy = floatPtr[1] * mat.Height / 640; float w = floatPtr[2] * mat.Width / 640; float h = floatPtr[3] * mat.Height / 640; boxes.Add(new Rectangle((int)(cx - w / 2), (int)(cy - h / 2), (int)w, (int)h)); } floatPtr += sizeDimension0; }
            VectorOfInt indices = new VectorOfInt(); VectorOfRect boxVector = new VectorOfRect(); boxVector.Push(boxes.ToArray()); VectorOfFloat confidencesVector = new VectorOfFloat(); confidencesVector.Push(confidences.ToArray()); DnnInvoke.NMSBoxes(boxVector, confidencesVector, confidenceThreshold, nmsThreshold, indices);
            foreach (int i in indices.ToArray())
            {
                Rectangle rect = boxes[i]; if (rect.X < 0) rect.X = 0; if (rect.Y < 0) rect.Y = 0; Point centerPoint = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2); double Z = ComputeZvalue(rect, disparityData, mat.Width); string zValue = Z.ToString("f2") + "m";
                Mat target = (!cbDisparity.Checked || disparityMat == null) ? mat : disparityMat; CvInvoke.Rectangle(target, rect, new MCvScalar(0, 255, 0), 2); CvInvoke.Circle(target, centerPoint, 5, new MCvScalar(255, 0, 0), -1, LineType.EightConnected, 0); CvInvoke.PutText(target, zValue, centerPoint, FontFace.HersheyTriplex, fontSize, new Bgr(255, 255, 255).MCvScalar, fontThick, LineType.EightConnected, false);
            }
        }

        unsafe private double ComputeZvalue(Rectangle rect, ushort* disparityData, int imgWidth)
        {
            List<double> zList = new List<double>(); int cornerX = rect.X, cornerY = rect.Y, width = rect.Width + rect.X, height = rect.Height + rect.Y;
            for (int row = cornerY; row < height; row++) for (int col = cornerX; col < width; col++) if (disparityData[row * imgWidth + col] > 0) { double disparityActual = disparityData[row * imgWidth + col] * stereoCameraParameters.disparityScaleFactor + stereoCameraParameters.coordinateOffset; zList.Add(stereoCameraParameters.focalLength * stereoCameraParameters.baseline / disparityActual); }
            return GetMedian(zList);
        }
        private double GetMedian(List<double> list)
        {
            if (list == null || list.Count == 0) return 0; var sorted = list.OrderBy(x => x).ToList(); int count = sorted.Count; return count % 2 == 1 ? sorted[count / 2] : (sorted[(count / 2) - 1] + sorted[count / 2]) / 2.0;
        }
        private void DataPalette(ushort minValue, ushort maxValue)
        {
            int range = maxValue - minValue + 1; const int Step = 6; int stepRange = range / Step; List<int> stepList = new List<int>(); for (int i = 0; i < Step; i++) stepList.Add(stepRange * (i + 1)); double pixelOfperValue = (double)byte.MaxValue / stepRange;
            for (int j = 0; j < ushort.MaxValue; j++)
            {
                if (j <= stepList[0]) { colorList[j].R = (byte)(j * pixelOfperValue); colorList[j].G = 0; colorList[j].B = 0; colorArr[j][0] = (byte)(j * pixelOfperValue); colorArr[j][1] = 0; colorArr[j][2] = 0; }
                else if (j <= stepList[1]) { int v = j - stepList[0]; colorList[j].R = 255; colorList[j].G = (byte)(v * pixelOfperValue); colorList[j].B = 0; colorArr[j][0] = 255; colorArr[j][1] = (byte)(v * pixelOfperValue); colorArr[j][2] = 0; }
                else if (j <= stepList[2]) { int v = j - stepList[1]; colorList[j].R = (byte)(255 - (byte)(v * pixelOfperValue)); colorList[j].G = 255; colorList[j].B = 0; colorArr[j][0] = colorList[j].R; colorArr[j][1] = 255; colorArr[j][2] = 0; }
                else if (j <= stepList[3]) { int v = j - stepList[2]; colorList[j].R = 0; colorList[j].G = 255; colorList[j].B = (byte)(v * pixelOfperValue); colorArr[j][0] = 0; colorArr[j][1] = 255; colorArr[j][2] = colorList[j].B; }
                else if (j <= stepList[5]) { int v = j - stepList[3]; colorList[j].R = 0; colorList[j].G = (byte)(255 - (byte)(v * pixelOfperValue)); colorList[j].B = 255; colorArr[j][0] = 0; colorArr[j][1] = colorList[j].G; colorArr[j][2] = 255; }
                else { int v = j - stepList[5]; colorList[j].R = colorList[j].G = colorList[j].B = (byte)(v * pixelOfperValue); colorArr[j][0] = colorArr[j][1] = colorArr[j][2] = (byte)(v * pixelOfperValue); }
            }
        }
        private void InvokeDisplay(Bitmap bmp, int imgWidth, int imgHeight) { pBox.Image = bmp; pBox.Invalidate(); }
    }
}
