using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SpinnakerNET;
using SpinnakerNET.GenApi;
using Emgu.CV;
using Emgu.CV.Dnn;
using Emgu.CV.Structure;
using Emgu.CV.Util;

namespace CSharp_Bumblebee
{
    public partial class Form1 : Form
    {
        private bool capImgComplete = true;
        private ManagedSystem system;
        private IManagedCamera cam;
        private ManagedImage leftImg;
        private ManagedImage rightImg;
        private ManagedImage dispImg;
        private ManagedImage leftRectImg;
        private Net net;

        public Form1()
        {
            InitializeComponent();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            try
            {
                system = new ManagedSystem();
                var camList = system.GetCameras();
                if (camList.Count == 0)
                {
                    MessageBox.Show("No camera detected.");
                    return;
                }
                cam = camList[0];
                cam.Init();
                net = DnnInvoke.ReadNetFromONNX("yolov8n.onnx");
                net.SetPreferableBackend(Emgu.CV.Dnn.Backend.Cuda);
                net.SetPreferableTarget(Emgu.CV.Dnn.Target.Cuda);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void btnStart_Click(object sender, EventArgs e)
        {
            if (!capImgComplete) return;
            capImgComplete = false;
            backgroundWorker1.RunWorkerAsync();
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            capImgComplete = true;
        }

        private void backgroundWorker1_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                cam.BeginAcquisition();
                while (!capImgComplete)
                {
                    using (IManagedImage raw = cam.GetNextImage(3000))
                    {
                        if (raw.IsIncomplete) continue;
                        using (IManagedImage converted = raw.Convert(PixelFormatEnums.BGR8))
                        {
                            Bitmap bmp = new Bitmap((int)converted.Width, (int)converted.Height,
                                (int)converted.Stride, PixelFormat.Format24bppRgb, converted.DataPtr);
                            Bitmap display = (Bitmap)bmp.Clone();
                            PersionDetect(display);
                            BeginInvoke(new Action(() =>
                            {
                                var old = pictureBox1.Image;
                                pictureBox1.Image = display;
                                old?.Dispose();
                            }));
                        }
                    }
                }
                cam.EndAcquisition();
            }
            catch (Exception ex)
            {
                BeginInvoke(new Action(() => MessageBox.Show(ex.Message)));
            }
        }

        private void PersionDetect(Bitmap bitmap)
        {
            using (Mat frame = bitmap.ToMat())
            using (Mat resized = new Mat())
            {
                CvInvoke.Resize(frame, resized, new Size(640, 640));
                using (Mat blob = DnnInvoke.BlobFromImage(resized, 1.0 / 255.0, new Size(640, 640), new MCvScalar(), true, false))
                {
                    net.SetInput(blob);
                    using (Mat output = net.Forward())
                    {
                        DrawDetections(frame, output, bitmap.Width / 640f, bitmap.Height / 640f);
                    }
                }
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.DrawImage(frame.ToBitmap(), 0, 0);
                }
            }
        }

        private unsafe void DrawDetections(Mat frame, Mat output, float xScale, float yScale)
        {
            int rows = output.SizeOfDimension[2];
            int dimensions = output.SizeOfDimension[1];
            float* data = (float*)output.DataPointer.ToPointer();
            var boxes = new List<Rectangle>();
            var scores = new List<float>();

            for (int i = 0; i < rows; i++)
            {
                float score = data[4 * rows + i];
                if (score < 0.5f) continue;

                float cx = data[0 * rows + i] * xScale;
                float cy = data[1 * rows + i] * yScale;
                float w = data[2 * rows + i] * xScale;
                float h = data[3 * rows + i] * yScale;
                Rectangle rect = new Rectangle((int)(cx - w / 2), (int)(cy - h / 2), (int)w, (int)h);
                boxes.Add(rect);
                scores.Add(score);
            }

            if (boxes.Count == 0) return;
            int[] indices = DnnInvoke.NMSBoxes(boxes.ToArray(), scores.ToArray(), 0.5f, 0.4f);
            foreach (int index in indices)
            {
                Rectangle r = boxes[index];
                CvInvoke.Rectangle(frame, r, new MCvScalar(0, 255, 0), 2);
                double z = ComputeZvalue(r);
                CvInvoke.PutText(frame, $"{z:F2} m", new Point(r.Left, Math.Max(20, r.Top - 8)),
                    Emgu.CV.CvEnum.FontFace.HersheySimplex, 0.8, new MCvScalar(0, 255, 0), 2);
            }
        }

        private double ComputeZvalue(Rectangle personRect)
        {
            if (dispImg == null) return 0;
            var values = new List<double>();
            int left = Math.Max(0, personRect.Left);
            int top = Math.Max(0, personRect.Top);
            int right = Math.Min((int)dispImg.Width, personRect.Right);
            int bottom = Math.Min((int)dispImg.Height, personRect.Bottom);
            for (int y = top; y < bottom; y++)
            {
                for (int x = left; x < right; x++)
                {
                    double z = 0;
                    if (z > 0) values.Add(z);
                }
            }
            if (values.Count == 0) return 0;
            values.Sort();
            return values[values.Count / 2];
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            capImgComplete = true;
            try { cam?.EndAcquisition(); } catch { }
            cam?.DeInit();
            cam?.Dispose();
            system?.Dispose();
            net?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
