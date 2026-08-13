using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;

namespace CSharp_Bumblebee
{
    public partial class Form1
    {
        // A new detection must be present in two consecutive pose inferences before
        // it is allowed onto the exhibition overlay. This removes most one-frame
        // false positives without changing the YOLO confidence threshold.
        private const int PoseConfirmConsecutiveHits = 2;

        // Once a person has been confirmed, tolerate two missed pose updates so a
        // brief occlusion or a single weak YOLO result does not make the skeleton blink.
        private const int PoseConfirmedMaxMisses = 2;

        // Cheap anatomical gate before temporal confirmation. A real person should
        // normally provide several keypoints including at least part of the torso.
        private const int PoseMinimumValidKeypoints = 5;
        private const int PoseMinimumCoreKeypoints = 2;

        private readonly List<TemporalPoseTrack> temporalPoseTracks =
            new List<TemporalPoseTrack>();

        private int nextTemporalPoseTrackId = 1;
        private int rawPosePeopleCount;
        private int structuredPosePeopleCount;

        private void ResetPoseTemporalFilter()
        {
            temporalPoseTracks.Clear();
            nextTemporalPoseTrackId = 1;
            Volatile.Write(ref rawPosePeopleCount, 0);
            Volatile.Write(ref structuredPosePeopleCount, 0);
        }

        private List<PosePerson> UpdateTemporalPoseFilter(
            List<PosePerson> detections,
            int imageWidth,
            int imageHeight)
        {
            int rawCount = detections != null ? detections.Count : 0;
            Volatile.Write(ref rawPosePeopleCount, rawCount);

            List<PosePerson> validDetections = new List<PosePerson>();
            if (detections != null)
            {
                foreach (PosePerson detection in detections)
                {
                    if (PassesPoseStructureGate(detection, imageWidth, imageHeight))
                        validDetections.Add(detection);
                }
            }

            Volatile.Write(ref structuredPosePeopleCount, validDetections.Count);

            // Age every track first. A successful match below resets Misses to zero.
            foreach (TemporalPoseTrack track in temporalPoseTracks)
            {
                track.MatchedThisUpdate = false;
                track.Misses++;
            }

            foreach (PosePerson detection in validDetections)
            {
                TemporalPoseTrack track = FindBestTemporalPoseTrack(detection);

                if (track == null)
                {
                    temporalPoseTracks.Add(new TemporalPoseTrack
                    {
                        Id = nextTemporalPoseTrackId++,
                        LastPose = detection,
                        ConsecutiveHits = 1,
                        Misses = 0,
                        Confirmed = false,
                        MatchedThisUpdate = true
                    });

                    continue;
                }

                // Misses == 1 means this track was seen on the immediately preceding
                // inference (we incremented it once at the start of this update).
                // Anything larger breaks the consecutive-hit streak.
                track.ConsecutiveHits = track.Misses == 1
                    ? track.ConsecutiveHits + 1
                    : 1;

                track.LastPose = detection;
                track.Misses = 0;
                track.MatchedThisUpdate = true;

                if (!track.Confirmed &&
                    track.ConsecutiveHits >= PoseConfirmConsecutiveHits)
                {
                    track.Confirmed = true;
                }
            }

            // Unconfirmed candidates must be consecutive, so remove them as soon as
            // one pose update misses. Confirmed people receive a short grace period.
            temporalPoseTracks.RemoveAll(track =>
                (!track.Confirmed && track.Misses > 0) ||
                (track.Confirmed && track.Misses > PoseConfirmedMaxMisses));

            List<PosePerson> confirmedPeople = new List<PosePerson>();
            foreach (TemporalPoseTrack track in temporalPoseTracks)
            {
                if (track.Confirmed && track.LastPose != null)
                    confirmedPeople.Add(track.LastPose);
            }

            return confirmedPeople;
        }

        private bool PassesPoseStructureGate(
            PosePerson person,
            int imageWidth,
            int imageHeight)
        {
            if (person == null ||
                person.Keypoints == null ||
                person.Keypoints.Length < KeypointCount ||
                person.Box.IsEmpty)
            {
                return false;
            }

            int validCount = 0;
            for (int i = 0; i < person.Keypoints.Length; i++)
            {
                if (IsValidKeypoint(person.Keypoints[i], imageWidth, imageHeight))
                    validCount++;
            }

            if (validCount < PoseMinimumValidKeypoints)
                return false;

            // COCO pose core: left/right shoulder (5,6), left/right hip (11,12).
            int coreCount = 0;
            if (IsValidKeypoint(person.Keypoints[5], imageWidth, imageHeight))
                coreCount++;
            if (IsValidKeypoint(person.Keypoints[6], imageWidth, imageHeight))
                coreCount++;
            if (IsValidKeypoint(person.Keypoints[11], imageWidth, imageHeight))
                coreCount++;
            if (IsValidKeypoint(person.Keypoints[12], imageWidth, imageHeight))
                coreCount++;

            if (coreCount < PoseMinimumCoreKeypoints)
                return false;

            // Require at least one shoulder. This rejects many object-shaped false
            // positives while still allowing a partially occluded or seated person.
            bool hasShoulder =
                IsValidKeypoint(person.Keypoints[5], imageWidth, imageHeight) ||
                IsValidKeypoint(person.Keypoints[6], imageWidth, imageHeight);

            return hasShoulder;
        }

        private TemporalPoseTrack FindBestTemporalPoseTrack(PosePerson detection)
        {
            TemporalPoseTrack bestTrack = null;
            double bestScore = double.NegativeInfinity;
            Point detectionCenter = GetBoxCenter(detection.Box);

            foreach (TemporalPoseTrack track in temporalPoseTracks)
            {
                if (track.MatchedThisUpdate || track.LastPose == null)
                    continue;

                Rectangle previousBox = track.LastPose.Box;
                Point previousCenter = GetBoxCenter(previousBox);

                double dx = detectionCenter.X - previousCenter.X;
                double dy = detectionCenter.Y - previousCenter.Y;
                double centerDistance = Math.Sqrt(dx * dx + dy * dy);

                double referenceSize = Math.Max(
                    50.0,
                    Math.Max(
                        Math.Max(previousBox.Width, previousBox.Height),
                        Math.Max(detection.Box.Width, detection.Box.Height)));

                double maxCenterDistance = referenceSize * 0.70;
                double iou = ComputeIntersectionOverUnion(previousBox, detection.Box);

                // Either meaningful overlap or a reasonably small center movement is
                // enough to match. This works for walking people without requiring a
                // heavyweight tracker.
                if (iou < 0.08 && centerDistance > maxCenterDistance)
                    continue;

                double normalizedDistance = centerDistance / maxCenterDistance;
                double score = iou * 2.0 - normalizedDistance * 0.35;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestTrack = track;
                }
            }

            return bestTrack;
        }

        private static Point GetBoxCenter(Rectangle box)
        {
            return new Point(
                box.X + box.Width / 2,
                box.Y + box.Height / 2);
        }

        private static double ComputeIntersectionOverUnion(Rectangle a, Rectangle b)
        {
            Rectangle intersection = Rectangle.Intersect(a, b);
            if (intersection.IsEmpty)
                return 0;

            double intersectionArea =
                (double)intersection.Width * intersection.Height;
            double unionArea =
                (double)a.Width * a.Height +
                (double)b.Width * b.Height -
                intersectionArea;

            return unionArea > 0
                ? intersectionArea / unionArea
                : 0;
        }

        private int GetRawPosePeopleCount()
        {
            return Volatile.Read(ref rawPosePeopleCount);
        }

        private int GetStructuredPosePeopleCount()
        {
            return Volatile.Read(ref structuredPosePeopleCount);
        }
    }

    internal sealed class TemporalPoseTrack
    {
        public int Id;
        public PosePerson LastPose;
        public int ConsecutiveHits;
        public int Misses;
        public bool Confirmed;
        public bool MatchedThisUpdate;
    }
}
