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
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImport("Gdi32.dll", EntryPoint = "CreateRoundRectRgn")]
        private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int ew, int eh);

        private const int YoloSize = 640;
        private const float ConfidenceThreshold = 0.50f;
        private const float KeypointThreshold = 0.35f;
        private const float NmsThreshold = 0.45f;
        private const int PoseOutputChannels = 56;
        private const int KeypointCount = 17;

        private static readonly int[,] SkeletonEdges = { {0,1},{0,2},{1,3},{2,4},{5,6},{5,7},{7,9},{6,8},{8,10},{5,11},{6,12},{11,12},{11,13},{13,15},{12,14},{14,16} };

        private Panel titleBar; private Label titleLabel; private Button closeButton, maximizeButton, minimizeButton, connectBtn, startBtn;
        private IManagedCamera cam; private bool connected, started, capImg, capImgComplete;
        private StereoCameraParameters stereoCameraParameters; private double fontSize; private int circleSize, fontThick;

        public Form1()
        {
            if (!SetDllDirectory(@"..\Libraries")) throw new Win32Exception();
            ManagedSystem system = new ManagedSystem(); ManagedCameraList camList = system.GetCameras();
            if (camList.Count == 0) { camList.Clear(); system.Dispose(); MessageBox.Show("No cameras Detected!"); Environment.Exit(Environment.ExitCode); }
            cam = camList[0]; stereoCameraParameters = new StereoCameraParameters(); InitializeComponent();
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen; BackColor = Color.White; Padding = new Padding(1);
            InitializeTitleBar(); InitializeBody(); Load += Form1_Load; SizeChanged += Form1_SizeChanged; pBoxLogo.Image = Bitmap.FromFile("APO_LOGO2.jpg");
        }

        private void Form1_Load(object sender, EventArgs e) { UpdateWindowRegion(); UpdateMaximizeButton(); }
        private void InitializeTitleBar()
        {
            titleBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(33,150,243) }; titleBar.MouseDown += TitleBar_MouseDown; titleBar.DoubleClick += (s,e) => ToggleMaximize(); Controls.Add(titleBar); titleBar.BringToFront();
            titleLabel = new Label { Text = "Teledyne FLIR BumbleBee Demo", ForeColor = Color.White, Font = new Font("Segoe UI",12,FontStyle.Bold), Location = new Point(10,10), AutoSize = true }; titleLabel.MouseDown += TitleBar_MouseDown; titleBar.Controls.Add(titleLabel);
            minimizeButton = CreateTitleButton("—"); minimizeButton.Click += (s,e) => WindowState = FormWindowState.Minimized; titleBar.Controls.Add(minimizeButton);
            maximizeButton = CreateTitleButton("□"); maximizeButton.Font = new Font("Segoe UI Symbol",14); maximizeButton.Click += (s,e) => ToggleMaximize(); titleBar.Controls.Add(maximizeButton);
            closeButton = CreateTitleButton("✕"); closeButton.Click += (s,e) => Close(); titleBar.Controls.Add(closeButton);
        }
        private Button CreateTitleButton(string text) { Button b = new Button { Text=text, FlatStyle=FlatStyle.Flat, ForeColor=Color.White, BackColor=Color.Transparent, Size=new Size(44,40), Dock=DockStyle.Right, TabStop=false }; b.FlatAppearance.BorderSize=0; b.FlatAppearance.MouseOverBackColor=Color.FromArgb(55,170,255); return b; }
        private void InitializeBody()
        {
            connectBtn = new Button { Text="連線", Size=new Size(120,40), Location=new Point((ClientSize.Width-300)/2,80), BackColor=Color.FromArgb(33,150,243), ForeColor=Color.White, FlatStyle=FlatStyle.Flat }; connectBtn.Click += connectBtn_Click; Controls.Add(connectBtn); connectBtn.BringToFront();
            startBtn = new Button { Text="開始取像", Size=new Size(120,40), Location=new Point(ClientSize.Width/2,80), BackColor=Color.FromArgb(33,150,243), ForeColor=Color.White, FlatStyle=FlatStyle.Flat, Enabled=false }; startBtn.Click += startBtn_Click; Controls.Add(startBtn); startBtn.BringToFront();
        }
        private void ToggleMaximize() { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; }
        private void Form1_SizeChanged(object sender, EventArgs e) { UpdateWindowRegion(); UpdateMaximizeButton(); }
        private void UpdateMaximizeButton() { if (maximizeButton != null) maximizeButton.Text = WindowState == FormWindowState.Maximized ? "❐" : "□"; }
        private void UpdateWindowRegion() { if (WindowState == FormWindowState.Maximized) { Region=null; return; } if (Width>0 && Height>0) Region=Region.FromHrgn(CreateRoundRectRgn(0,0,Width,Height,20,20)); }
        private void TitleBar_MouseDown(object sender, MouseEventArgs e) { if (e.Button!=MouseButtons.Left || WindowState==FormWindowState.Maximized) return; ReleaseCapture(); SendMessage(Handle,0xA1,0x2,0); }

        private void GetStereoParameter(StereoCameraParameters p, INodeMap n) { p.coordinateOffset=(float)n.GetNode<IFloat>("Scan3dCoordinateOffset").Value; p.baseline=(float)n.GetNode<IFloat>("Scan3dBaseline").Value; p.focalLength=(float)n.GetNode<IFloat>("Scan3dFocalLength").Value; p.principalPointU=(float)n.GetNode<IFloat>("Scan3dPrincipalPointU").Value; p.principalPointV=(float)n.GetNode<IFloat>("Scan3dPrincipalPointV").Value; p.disparityScaleFactor=(float)n.GetNode<IFloat>("Scan3dCoordinateScale").Value; p.invalidDataFlag=n.GetNode<IBool>("Scan3dInvalidDataFlag").Value; p.invalidDataValue=(float)n.GetNode<IFloat>("Scan3dInvalidDataValue").Value; }
        private void SetBufferHandingMode(INodeMap n) { n.GetNode<IEnum>("StreamBufferHandlingMode").Value=StreamBufferHandlingModeEnum.NewestOnly.ToString(); }
        private void connectBtn_Click(object sender, EventArgs e) { if(!connected){cam.Init();INodeMap n=cam.GetNodeMap();for(int i=0;i<5;i++)SetBufferHandingMode(cam.GetTLStreamNodeMap(i));ResoultionCheck(n);GetStereoParameter(stereoCameraParameters,n);connectBtn.Text="斷線";startBtn.Enabled=true;connected=true;}else{cam.DeInit();connectBtn.Text="連線";startBtn.Enabled=false;connected=false;} }
        private void startBtn_Click(object sender, EventArgs e) { if(!started){capImg=true;capImgComplete=false;cam.BeginAcquisition();backgroundWorker1.RunWorkerAsync();startBtn.Text="停止取像";connectBtn.Enabled=false;started=true;}else{capImg=false;startBtn.Enabled=false;} }
        private void ResoultionCheck(INodeMap n) { IEnum r=n.GetNode<IEnum>("StereoResolution"); if(r.ToString()=="Full"){circleSize=5;fontSize=1;fontThick=2;}else if(r.ToString()=="Quarter"){circleSize=3;fontSize=.5;fontThick=1;}else{circleSize=4;fontSize=.8;fontThick=2;} }
        public delegate void InvokeDelegate(Bitmap bmp,int imgWidth,int imgHeight);

        private void backgroundWorker1_DoWork(object sender, DoWorkEventArgs e)
        {
            Net net = DnnInvoke.ReadNetFromONNX("yolov8n-pose.onnx"); net.SetPreferableBackend(Emgu.CV.Dnn.Backend.OpenCV); net.SetPreferableTarget(Target.Cpu);
            IManagedImageList imageList=new ManagedImageList(); IManagedImage rectifiedImg=new ManagedImage(); IManagedImage disparityImg=new ManagedImage();
            try
            {
                while(capImg)
                {
                    imageList=cam.GetNextImageSync(3000); IManagedImage ro=imageList.GetByPayloadType(ImagePayloadType.IMAGE_PAYLOAD_TYPE_RECTIFIED_SENSOR1); IManagedImage d=imageList.GetByPayloadType(ImagePayloadType.IMAGE_PAYLOAD_TYPE_DISPARITY_SENSOR1);
                    if(ro==null||d==null||ro.IsIncomplete||d.IsIncomplete){imageList.Release();continue;}
                    rectifiedImg.DeepCopy(ro); disparityImg.DeepCopy(d); ro.Dispose(); d.Dispose(); imageList.Release();
                    int w=(int)rectifiedImg.Width,h=(int)rectifiedImg.Height,stride=(int)rectifiedImg.Stride;
                    using(Mat bgr=new Mat()) using(Mat rgb=new Mat(h,w,DepthType.Cv8U,3,rectifiedImg.DataPtr,0))
                    {
                        CvInvoke.CvtColor(rgb,bgr,ColorConversion.Rgb2Bgr);
                        unsafe { PoseDetect(net,(ushort*)disparityImg.NativeData,bgr); }
                        using(Bitmap bmp=new Bitmap(w,h,stride,PixelFormat.Format24bppRgb,bgr.DataPointer)) { pBox.BeginInvoke(new InvokeDelegate(InvokeDisplay),new Bitmap(bmp),w,h); }
                    }
                }
            }
            finally { net.Dispose();rectifiedImg.Dispose();disparityImg.Dispose();capImgComplete=true;BeginInvoke(new Action(()=>{try{cam.EndAcquisition();}catch{}startBtn.Text="開始取像";startBtn.Enabled=true;connectBtn.Enabled=true;started=false;})); }
        }

        private Mat Letterbox(Mat src,out float scale,out int padX,out int padY) { scale=Math.Min(YoloSize/(float)src.Width,YoloSize/(float)src.Height);int nw=(int)Math.Round(src.Width*scale),nh=(int)Math.Round(src.Height*scale);padX=(YoloSize-nw)/2;padY=(YoloSize-nh)/2;Mat dst=new Mat(YoloSize,YoloSize,DepthType.Cv8U,3);dst.SetTo(new MCvScalar(114,114,114));using(Mat resized=new Mat()){CvInvoke.Resize(src,resized,new Size(nw,nh));using(Mat roi=new Mat(dst,new Rectangle(padX,padY,nw,nh)))resized.CopyTo(roi);}return dst; }

        private unsafe void PoseDetect(Net net, ushort* disparityData, Mat mat)
        {
            float scale; int padX,padY;
            using(Mat input=Letterbox(mat,out scale,out padX,out padY)) using(Mat blob=DnnInvoke.BlobFromImage(input,1.0/255.0,new Size(YoloSize,YoloSize),new MCvScalar(),true,false))
            {
                net.SetInput(blob);
                using(Mat output=net.Forward()) using(Mat reshaped=output.Reshape(1,PoseOutputChannels)) using(Mat transposed=new Mat())
                {
                    CvInvoke.Transpose(reshaped,transposed); List<PosePerson> people=new List<PosePerson>(); List<Rectangle> boxes=new List<Rectangle>(); List<float> scores=new List<float>(); float* data=(float*)transposed.DataPointer.ToPointer(); int rows=transposed.Rows,cols=transposed.Cols;
                    for(int i=0;i<rows;i++,data+=cols)
                    {
                        float score=data[4]; if(score<ConfidenceThreshold)continue;
                        float cx=(data[0]-padX)/scale,cy=(data[1]-padY)/scale,bw=data[2]/scale,bh=data[3]/scale;
                        Rectangle box=ClampRect(new Rectangle((int)(cx-bw/2),(int)(cy-bh/2),(int)bw,(int)bh),mat.Width,mat.Height); if(box.IsEmpty)continue;
                        PosePerson person=new PosePerson{Box=box,Keypoints=new PoseKeypoint[KeypointCount]};
                        for(int k=0;k<KeypointCount;k++){int o=5+k*3;person.Keypoints[k]=new PoseKeypoint{X=(data[o]-padX)/scale,Y=(data[o+1]-padY)/scale,Confidence=data[o+2]};}
                        people.Add(person);boxes.Add(box);scores.Add(score);
                    }
                    if(people.Count==0)return;
                    using(VectorOfRect bv=new VectorOfRect(boxes.ToArray())) using(VectorOfFloat sv=new VectorOfFloat(scores.ToArray())) using(VectorOfInt indices=new VectorOfInt())
                    {
                        DnnInvoke.NMSBoxes(bv,sv,ConfidenceThreshold,NmsThreshold,indices);
                        foreach(int index in indices.ToArray())
                        {
                            PosePerson person=people[index]; DrawSkeleton(mat,person.Keypoints);
                            Point center=new Point(person.Box.X+person.Box.Width/2,person.Box.Y+person.Box.Height/2);
                            double distance=ComputeZvalue(person.Box,disparityData,mat.Width,mat.Height);
                            CvInvoke.Circle(mat,center,circleSize+1,new MCvScalar(0,255,255),-1);
                            if(distance>0)CvInvoke.PutText(mat,distance.ToString("F2")+"m",new Point(center.X+8,center.Y-8),FontFace.HersheyTriplex,fontSize,new MCvScalar(255,255,255),fontThick);
                        }
                    }
                }
            }
        }

        private void DrawSkeleton(Mat mat, PoseKeypoint[] keypoints)
        {
            for(int i=0;i<SkeletonEdges.GetLength(0);i++){PoseKeypoint a=keypoints[SkeletonEdges[i,0]],b=keypoints[SkeletonEdges[i,1]];if(IsValidKeypoint(a,mat.Width,mat.Height)&&IsValidKeypoint(b,mat.Width,mat.Height))CvInvoke.Line(mat,new Point((int)a.X,(int)a.Y),new Point((int)b.X,(int)b.Y),new MCvScalar(0,255,0),2);}
            foreach(PoseKeypoint p in keypoints)if(IsValidKeypoint(p,mat.Width,mat.Height))CvInvoke.Circle(mat,new Point((int)p.X,(int)p.Y),circleSize,new MCvScalar(0,255,255),-1);
        }
        private bool IsValidKeypoint(PoseKeypoint p,int w,int h){return p.Confidence>=KeypointThreshold&&p.X>=0&&p.X<w&&p.Y>=0&&p.Y<h;}
        private Rectangle ClampRect(Rectangle r,int width,int height){int l=Math.Max(0,r.Left),t=Math.Max(0,r.Top),rr=Math.Min(width,r.Right),bb=Math.Min(height,r.Bottom);return rr>l&&bb>t?Rectangle.FromLTRB(l,t,rr,bb):Rectangle.Empty;}
        private unsafe double ComputeZvalue(Rectangle personRect,ushort* disparityData,int imgWidth,int imgHeight){Rectangle person=ClampRect(personRect,imgWidth,imgHeight);if(person.IsEmpty)return 0;int rw=Math.Max(1,(int)(person.Width*.45)),rh=Math.Max(1,(int)(person.Height*.45));int left=person.X+(person.Width-rw)/2,top=person.Y+(int)(person.Height*.25);Rectangle roi=ClampRect(new Rectangle(left,top,rw,rh),imgWidth,imgHeight);List<double> distances=new List<double>(Math.Max(32,roi.Width*roi.Height/16));for(int y=roi.Top;y<roi.Bottom;y+=2)for(int x=roi.Left;x<roi.Right;x+=2){ushort raw=disparityData[y*imgWidth+x];if(raw==0)continue;if(stereoCameraParameters.invalidDataFlag&&Math.Abs(raw-stereoCameraParameters.invalidDataValue)<.5)continue;double disp=raw*stereoCameraParameters.disparityScaleFactor+stereoCameraParameters.coordinateOffset;if(disp<=0)continue;double distance=stereoCameraParameters.focalLength*stereoCameraParameters.baseline/disp;if(distance>0&&distance<100000)distances.Add(distance);}return GetMedianInPlace(distances);}
        private double GetMedianInPlace(List<double> values){if(values==null||values.Count==0)return 0;values.Sort();int m=values.Count/2;return values.Count%2==0?(values[m-1]+values[m])*.5:values[m];}
        private void InvokeDisplay(Bitmap bmp,int imgWidth,int imgHeight){Bitmap old=pBox.Image as Bitmap;pBox.Image=bmp;old?.Dispose();}
        private void Form1_FormClosing(object sender,FormClosingEventArgs e){capImg=false;try{if(started)cam.EndAcquisition();}catch{}try{if(connected)cam.DeInit();}catch{}cam?.Dispose();}
        private void backgroundWorker1_RunWorkerCompleted(object sender,RunWorkerCompletedEventArgs e){}
    }

    public class StereoCameraParameters { public float coordinateOffset,baseline,focalLength,principalPointU,principalPointV,disparityScaleFactor; public bool invalidDataFlag; public float invalidDataValue; }
    internal struct PoseKeypoint { public float X,Y,Confidence; }
    internal class PosePerson { public Rectangle Box; public PoseKeypoint[] Keypoints; }
}
