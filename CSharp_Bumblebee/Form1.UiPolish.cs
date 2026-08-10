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
            // BGR colors chosen to remain bright and easy to distinguish on camera images.
            new MCvScalar(118, 230, 0),   // emerald
            new MCvScalar(212, 188, 0),   // cyan
            new MCvScalar(0, 152, 255),   // orange
            new MCvScalar(99, 30, 233),   // pink
            new MCvScalar(7, 193, 255),   // amber
            new MCvScalar(245, 165, 66),  // blue
            new MCvScalar(188, 71, 171),  // violet
            new MCvScalar(57, 220, 205)   // lime
        };

        // Keep debug visible while tuning. F3 instantly toggles it for exhibition/demo mode.
        private bool showDebugOverlay = true;
        private bool uiPolishInitialized;

        private void InitializeUiPolish()
        {
            if (uiPolishInitialized)
                return;

            uiPolishInitialized = true;
            KeyPreview = true;
            KeyDown += Form1_UiPolishKeyDown;

            // The title panel already supports double click. The label receives mouse
            // messages itself, so explicitly give it the same maximize/restore action.
            if (titleLabel != null)
                titleLabel.DoubleClick += (s, e) => ToggleMaximize();

            ApplyExhibitionStyle();
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

            // Rounded action buttons give the header a cleaner exhibition look.
            if (button.Width > 0 && button.Height > 0)
                button.Region = Region.FromHrgn(
                    CreateRoundRectRgn(0, 0, button.Width, button.Height, 12, 12));
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
            double bestDistanceSquared = DistanceTrackMatchPixels * DistanceTrackMatchPixels;

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
        /// Draw a presentation layer over the existing pose rendering. The underlying
        /// tracking/distance code remains unchanged, while each tracked person receives
        /// a stable color based on DistanceTrack.Id.
        /// </summary>
        private void ApplyUiPolishOverlay(Mat mat)
        {
            if (mat == null || mat.IsEmpty)
                return;

            var people = GetPoseSnapshot();
            int fallbackTrackId = 1;

            foreach (PosePerson person in people)
            {
                Point center = GetTorsoCenter(person, mat.Width, mat.Height);
                DistanceTrack track = FindMatchedTrack(center);
                int trackId = track != null ? track.Id : fallbackTrackId++;
                MCvScalar color = GetPersonColor(trackId);

                DrawPresentationSkeleton(mat, person.Keypoints, color);

                CvInvoke.Circle(
                    mat,
                    center,
                    Math.Max(circleSize + 2, 4),
                    color,
                    -1,
                    LineType.AntiAlias);

                if (track != null && track.HasDistance && track.SmoothedDistance > 0)
                    DrawDistanceBadge(mat, center, track.SmoothedDistance, color);
            }
        }

        private void DrawPresentationSkeleton(
            Mat mat,
            PoseKeypoint[] keypoints,
            MCvScalar color)
        {
            int lineThickness = Math.Max(2, fontThick + 2);
            int jointRadius = Math.Max(circleSize + 1, 3);

            for (int i = 0; i < SkeletonEdges.GetLength(0); i++)
            {
                PoseKeypoint a = keypoints[SkeletonEdges[i, 0]];
                PoseKeypoint b = keypoints[SkeletonEdges[i, 1]];

                if (!IsValidKeypoint(a, mat.Width, mat.Height) ||
                    !IsValidKeypoint(b, mat.Width, mat.Height))
                    continue;

                CvInvoke.Line(
                    mat,
                    new Point((int)a.X, (int)a.Y),
                    new Point((int)b.X, (int)b.Y),
                    color,
                    lineThickness,
                    LineType.AntiAlias);
            }

            foreach (PoseKeypoint point in keypoints)
            {
                if (!IsValidKeypoint(point, mat.Width, mat.Height))
                    continue;

                CvInvoke.Circle(
                    mat,
                    new Point((int)point.X, (int)point.Y),
                    jointRadius,
                    color,
                    -1,
                    LineType.AntiAlias);

                // A tiny white center gives joints definition on both bright and dark clothing.
                CvInvoke.Circle(
                    mat,
                    new Point((int)point.X, (int)point.Y),
                    Math.Max(1, jointRadius / 3),
                    new MCvScalar(245, 245, 245),
                    -1,
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

            badgeX = Math.Max(2, Math.Min(mat.Width - badgeWidth - 2, badgeX));
            badgeY = Math.Max(2, Math.Min(mat.Height - badgeHeight - 2, badgeY));

            Rectangle badge = new Rectangle(
                badgeX,
                badgeY,
                badgeWidth,
                badgeHeight);

            // The filled panel intentionally covers the legacy white distance text below it.
            CvInvoke.Rectangle(mat, badge, new MCvScalar(22, 26, 32), -1);
            CvInvoke.Rectangle(mat, badge, color, Math.Max(1, fontThick + 1));

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
