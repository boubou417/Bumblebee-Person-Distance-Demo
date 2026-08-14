using System;
using System.Drawing;
using System.Windows.Forms;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace CSharp_Bumblebee
{
    public partial class Form1
    {
        private readonly MCvScalar[] personPalette =
        {
            new MCvScalar(118, 230, 0),
            new MCvScalar(212, 188, 0),
            new MCvScalar(0, 152, 255),
            new MCvScalar(99, 30, 233),
            new MCvScalar(7, 193, 255),
            new MCvScalar(245, 165, 66),
            new MCvScalar(188, 71, 171),
            new MCvScalar(57, 220, 205)
        };

        private bool showDebugOverlay = true;
        private bool uiPolishInitialized;

        private bool titleDragActive;
        private Point titleDragCursorStart;
        private Point titleDragFormStart;

        private void InitializeUiPolish()
        {
            if (uiPolishInitialized)
                return;

            uiPolishInitialized = true;
            KeyPreview = true;
            KeyDown += Form1_UiPolishKeyDown;

            ConfigurePolishedTitleBarInput(titleBar);
            ConfigurePolishedTitleBarInput(titleLabel);

            if (titleLabel != null)
            {
                titleLabel.Text = "Teledyne FLIR BumbleBee Demo  ·  Lightweight Detection";
                titleLabel.DoubleClick += (s, e) => ToggleMaximize();
            }

            ApplyExhibitionStyle();
        }

        private void ConfigurePolishedTitleBarInput(Control control)
        {
            if (control == null)
                return;

            control.MouseDown -= TitleBar_MouseDown;
            control.MouseDown += PolishedTitleBar_MouseDown;
            control.MouseMove += PolishedTitleBar_MouseMove;
            control.MouseUp += PolishedTitleBar_MouseUp;
        }

        private void PolishedTitleBar_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || e.Clicks > 1)
                return;

            if (WindowState != FormWindowState.Normal)
                return;

            titleDragActive = true;
            titleDragCursorStart = Cursor.Position;
            titleDragFormStart = Location;
        }

        private void PolishedTitleBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!titleDragActive || e.Button != MouseButtons.Left)
                return;

            Point cursor = Cursor.Position;
            int dx = cursor.X - titleDragCursorStart.X;
            int dy = cursor.Y - titleDragCursorStart.Y;

            if (Math.Abs(dx) < 3 && Math.Abs(dy) < 3)
                return;

            Location = new Point(
                titleDragFormStart.X + dx,
                titleDragFormStart.Y + dy);
        }

        private void PolishedTitleBar_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                titleDragActive = false;
        }

        private void ApplyExhibitionStyle()
        {
            BackColor = Color.FromArgb(246, 248, 251);

            if (titleBar != null)
                titleBar.BackColor = Color.FromArgb(24, 137, 226);

            if (titleLabel != null)
            {
                titleLabel.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
                titleLabel.ForeColor = Color.White;
            }

            if (headerPanel != null)
                headerPanel.BackColor = Color.White;

            if (imagePanel != null)
            {
                imagePanel.BackColor = Color.FromArgb(205, 216, 228);
                imagePanel.Padding = new Padding(2);
            }

            if (pBox != null)
                pBox.BackColor = Color.FromArgb(12, 16, 22);

            StyleActionButton(connectBtn);
            StyleActionButton(startBtn);

            if (cbDisparity != null)
            {
                cbDisparity.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
                cbDisparity.ForeColor = Color.FromArgb(73, 86, 103);
                cbDisparity.BackColor = Color.Transparent;
                cbDisparity.FlatStyle = FlatStyle.Standard;
                cbDisparity.Cursor = Cursors.Hand;
            }
        }

        private void StyleActionButton(Button button)
        {
            if (button == null)
                return;

            button.Font = new Font("Segoe UI", 10.0f, FontStyle.Bold);
            button.ForeColor = Color.White;
            button.BackColor = Color.FromArgb(33, 150, 243);
            button.FlatStyle = FlatStyle.Flat;
            button.Cursor = Cursors.Hand;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(25, 118, 210);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(20, 96, 171);

            if (button.Width > 0 && button.Height > 0)
            {
                button.Region = Region.FromHrgn(
                    CreateRoundRectRgn(
                        0,
                        0,
                        button.Width,
                        button.Height,
                        12,
                        12));
            }
        }

        private void Form1_UiPolishKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F11)
            {
                ToggleMaximize();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.F3)
            {
                showDebugOverlay = !showDebugOverlay;
                pBox?.Invalidate();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private MCvScalar GetPersonColor(int trackId)
        {
            if (personPalette.Length == 0)
                return new MCvScalar(0, 255, 0);

            int index = Math.Abs(trackId - 1) % personPalette.Length;
            return personPalette[index];
        }

        private DistanceTrack FindMatchedTrack(Point center)
        {
            DistanceTrack bestTrack = null;
            double bestDistanceSquared =
                DistanceTrackMatchPixels * DistanceTrackMatchPixels;

            foreach (DistanceTrack track in distanceTracks)
            {
                if (!track.MatchedThisFrame)
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

            return bestTrack;
        }

        /// <summary>
        /// Lightweight presentation layer. The AI worker now returns only person
        /// boxes; the existing distance pipeline still updates a chest/body-center
        /// distance track before this overlay is rendered.
        /// </summary>
        private void ApplyUiPolishOverlay(Mat mat)
        {
            if (mat == null || mat.IsEmpty)
                return;

            var people = GetPoseSnapshot();
            int fallbackTrackId = 1;

            foreach (PosePerson person in people)
            {
                Rectangle box = ClampRect(
                    person.Box,
                    mat.Width,
                    mat.Height);

                if (box.IsEmpty)
                    continue;

                Point center = new Point(
                    box.X + box.Width / 2,
                    box.Y + box.Height / 2);

                DistanceTrack track = FindMatchedTrack(center);
                int trackId = track != null
                    ? track.Id
                    : fallbackTrackId++;
                MCvScalar color = GetPersonColor(trackId);

                DrawPresentationPersonBox(mat, box, color);

                CvInvoke.Circle(
                    mat,
                    center,
                    Math.Max(circleSize + 2, 4),
                    color,
                    -1,
                    LineType.AntiAlias);

                if (track != null &&
                    track.HasDistance &&
                    track.SmoothedDistance > 0)
                {
                    DrawDistanceBadge(
                        mat,
                        center,
                        track.SmoothedDistance,
                        color);
                }
            }
        }

        private void DrawPresentationPersonBox(
            Mat mat,
            Rectangle box,
            MCvScalar color)
        {
            int thickness = Math.Max(2, fontThick + 2);

            CvInvoke.Rectangle(
                mat,
                box,
                color,
                thickness,
                LineType.AntiAlias);

            // Small exhibition-style label. It intentionally avoids confidence text
            // so the display stays clean and uses no additional detection bookkeeping.
            int labelHeight = Math.Max(20, 16 + fontThick * 2);
            int labelWidth = 72;
            int labelY = Math.Max(0, box.Top - labelHeight);
            Rectangle labelRect = new Rectangle(
                box.Left,
                labelY,
                Math.Min(labelWidth, Math.Max(1, mat.Width - box.Left)),
                Math.Min(labelHeight, Math.Max(1, mat.Height - labelY)));

            if (!labelRect.IsEmpty)
            {
                CvInvoke.Rectangle(
                    mat,
                    labelRect,
                    new MCvScalar(22, 26, 32),
                    -1);
                CvInvoke.Rectangle(
                    mat,
                    labelRect,
                    color,
                    Math.Max(1, fontThick));

                Point textOrigin = new Point(
                    labelRect.X + 6,
                    labelRect.Y + labelRect.Height - 6);

                CvInvoke.PutText(
                    mat,
                    "PERSON",
                    textOrigin,
                    FontFace.HersheySimplex,
                    0.42,
                    new MCvScalar(255, 255, 255),
                    1,
                    LineType.AntiAlias);
            }
        }

        private void DrawDistanceBadge(
            Mat mat,
            Point center,
            double distanceMeters,
            MCvScalar color)
        {
            string text = distanceMeters.ToString("F2") + "m";
            double textScale = Math.Max(0.55, fontSize);
            int textThickness = Math.Max(1, fontThick);

            double sizeScale = Math.Max(1.0, textScale / 0.55);
            int badgeWidth = (int)Math.Round(76 * sizeScale);
            int badgeHeight = (int)Math.Round(26 * sizeScale);
            int badgeX = center.X + 8;
            int badgeY = center.Y - badgeHeight - 6;

            badgeX = Math.Max(
                2,
                Math.Min(mat.Width - badgeWidth - 2, badgeX));
            badgeY = Math.Max(
                2,
                Math.Min(mat.Height - badgeHeight - 2, badgeY));

            Rectangle badge = new Rectangle(
                badgeX,
                badgeY,
                badgeWidth,
                badgeHeight);

            CvInvoke.Rectangle(
                mat,
                badge,
                new MCvScalar(22, 26, 32),
                -1);
            CvInvoke.Rectangle(
                mat,
                badge,
                color,
                Math.Max(1, fontThick + 1));

            Point textOrigin = new Point(
                badge.X + (int)Math.Round(9 * sizeScale),
                badge.Y + badge.Height - (int)Math.Round(7 * sizeScale));

            CvInvoke.PutText(
                mat,
                text,
                textOrigin,
                FontFace.HersheySimplex,
                textScale,
                new MCvScalar(255, 255, 255),
                textThickness,
                LineType.AntiAlias);
        }
    }
}
