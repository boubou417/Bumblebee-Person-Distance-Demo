using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;

namespace CSharp_Bumblebee
{
    public partial class Form1
    {
        private const int PoseConfirmConsecutiveHits = 2;
        private const int PoseConfirmedMaxMisses = 2;

        // Box-only duplicate suppression. Keep this conservative so two real people
        // standing close together are not merged simply because their boxes overlap.
        private const double DetectionDuplicateStrongIou = 0.72;
        private const double DetectionDuplicateCenterRatio = 0.10;
        private const double DetectionDuplicateMinSizeRatio = 0.55;
        private const double DetectionTrackCenterRatio = 0.62;

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
                    if (IsValidDetectionBox(detection, imageWidth, imageHeight))
                        validDetections.Add(detection);
                }
            }

            List<PosePerson> deduplicated = SuppressDuplicateDetections(
                validDetections);

            Volatile.Write(ref structuredPosePeopleCount, deduplicated.Count);

            foreach (TemporalPoseTrack track in temporalPoseTracks)
            {
                track.MatchedThisUpdate = false;
                track.Misses++;
            }

            foreach (PosePerson detection in deduplicated)
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

            temporalPoseTracks.RemoveAll(track =>
                (!track.Confirmed && track.Misses > 0) ||
                (track.Confirmed && track.Misses > PoseConfirmedMaxMisses));

            MergeDuplicateTemporalTracks();

            List<PosePerson> confirmedPeople = new List<PosePerson>();
            foreach (TemporalPoseTrack track in temporalPoseTracks)
            {
                if (track.Confirmed && track.LastPose != null)
                    confirmedPeople.Add(track.LastPose);
            }

            return confirmedPeople;
        }

        private bool IsValidDetectionBox(
            PosePerson person,
            int imageWidth,
            int imageHeight)
        {
            if (person == null || person.Box.IsEmpty)
                return false;

            Rectangle box = person.Box;
            if (box.Width < 12 || box.Height < 20)
                return false;

            if (box.Right <= 0 || box.Bottom <= 0 ||
                box.Left >= imageWidth || box.Top >= imageHeight)
            {
                return false;
            }

            return true;
        }

        private List<PosePerson> SuppressDuplicateDetections(
            List<PosePerson> detections)
        {
            List<PosePerson> kept = new List<PosePerson>();

            if (detections == null)
                return kept;

            foreach (PosePerson candidate in detections)
            {
                int duplicateIndex = -1;

                for (int i = 0; i < kept.Count; i++)
                {
                    if (AreLikelyDuplicateBoxes(kept[i].Box, candidate.Box, false))
                    {
                        duplicateIndex = i;
                        break;
                    }
                }

                if (duplicateIndex < 0)
                {
                    kept.Add(candidate);
                    continue;
                }

                // Without pose keypoint confidence, prefer the larger body extent.
                // It normally gives a more stable chest ROI for disparity distance.
                Rectangle existingBox = kept[duplicateIndex].Box;
                double existingArea =
                    (double)existingBox.Width * existingBox.Height;
                double candidateArea =
                    (double)candidate.Box.Width * candidate.Box.Height;

                if (candidateArea > existingArea)
                    kept[duplicateIndex] = candidate;
            }

            return kept;
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

                double maxCenterDistance =
                    referenceSize * DetectionTrackCenterRatio;
                double iou = ComputeIntersectionOverUnion(
                    previousBox,
                    detection.Box);

                if (iou < 0.05 && centerDistance > maxCenterDistance)
                    continue;

                double normalizedDistance =
                    centerDistance / Math.Max(1.0, maxCenterDistance);
                double score = iou * 2.2 - normalizedDistance * 0.40;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestTrack = track;
                }
            }

            return bestTrack;
        }

        private void MergeDuplicateTemporalTracks()
        {
            bool merged;

            do
            {
                merged = false;

                for (int i = 0; i < temporalPoseTracks.Count && !merged; i++)
                {
                    TemporalPoseTrack a = temporalPoseTracks[i];
                    if (a.LastPose == null)
                        continue;

                    for (int j = i + 1; j < temporalPoseTracks.Count; j++)
                    {
                        TemporalPoseTrack b = temporalPoseTracks[j];
                        if (b.LastPose == null)
                            continue;

                        if (!AreLikelyDuplicateBoxes(
                            a.LastPose.Box,
                            b.LastPose.Box,
                            true))
                        {
                            continue;
                        }

                        TemporalPoseTrack survivor;
                        TemporalPoseTrack loser;

                        if (a.Confirmed != b.Confirmed)
                        {
                            survivor = a.Confirmed ? a : b;
                            loser = a.Confirmed ? b : a;
                        }
                        else
                        {
                            // Preserve the older identity where possible so the
                            // presentation color remains stable after a duplicate.
                            survivor = a.Id <= b.Id ? a : b;
                            loser = a.Id <= b.Id ? b : a;
                        }

                        if (loser.MatchedThisUpdate && !survivor.MatchedThisUpdate)
                            survivor.LastPose = loser.LastPose;

                        survivor.Confirmed = survivor.Confirmed || loser.Confirmed;
                        survivor.ConsecutiveHits = Math.Max(
                            survivor.ConsecutiveHits,
                            loser.ConsecutiveHits);
                        survivor.Misses = Math.Min(
                            survivor.Misses,
                            loser.Misses);
                        survivor.MatchedThisUpdate =
                            survivor.MatchedThisUpdate || loser.MatchedThisUpdate;

                        temporalPoseTracks.Remove(loser);
                        merged = true;
                        break;
                    }
                }
            }
            while (merged);
        }

        private bool AreLikelyDuplicateBoxes(
            Rectangle a,
            Rectangle b,
            bool strict)
        {
            if (a.IsEmpty || b.IsEmpty)
                return false;

            double areaA = Math.Max(1.0, (double)a.Width * a.Height);
            double areaB = Math.Max(1.0, (double)b.Width * b.Height);
            double sizeRatio = Math.Min(areaA, areaB) / Math.Max(areaA, areaB);
            double iou = ComputeIntersectionOverUnion(a, b);

            Rectangle intersection = Rectangle.Intersect(a, b);
            double overlapOverSmaller = intersection.IsEmpty
                ? 0
                : ((double)intersection.Width * intersection.Height) /
                  Math.Min(areaA, areaB);

            Point centerA = GetBoxCenter(a);
            Point centerB = GetBoxCenter(b);
            double dx = centerA.X - centerB.X;
            double dy = centerA.Y - centerB.Y;
            double centerDistance = Math.Sqrt(dx * dx + dy * dy);

            double diagonalA = Math.Sqrt(
                (double)a.Width * a.Width + (double)a.Height * a.Height);
            double diagonalB = Math.Sqrt(
                (double)b.Width * b.Width + (double)b.Height * b.Height);
            double referenceDiagonal = Math.Max(
                40.0,
                Math.Max(diagonalA, diagonalB));

            double strongIou = strict
                ? 0.80
                : DetectionDuplicateStrongIou;
            double centerLimit = referenceDiagonal *
                (strict
                    ? DetectionDuplicateCenterRatio * 0.85
                    : DetectionDuplicateCenterRatio);
            double minimumSizeRatio = strict
                ? 0.65
                : DetectionDuplicateMinSizeRatio;

            if (iou >= strongIou && sizeRatio >= minimumSizeRatio)
                return true;

            // Catch the common nested-box case where one prediction covers most of
            // the same person but IoU stays modest because its extent is smaller.
            bool nestedDuplicate =
                overlapOverSmaller >= (strict ? 0.82 : 0.72) &&
                centerDistance <= centerLimit * 1.35 &&
                sizeRatio >= (strict ? 0.55 : 0.42);

            if (nestedDuplicate)
                return true;

            return centerDistance <= centerLimit &&
                   sizeRatio >= (strict ? 0.75 : 0.68) &&
                   iou >= (strict ? 0.55 : 0.42);
        }

        private static Point GetBoxCenter(Rectangle box)
        {
            return new Point(
                box.X + box.Width / 2,
                box.Y + box.Height / 2);
        }

        private static double ComputeIntersectionOverUnion(
            Rectangle a,
            Rectangle b)
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
