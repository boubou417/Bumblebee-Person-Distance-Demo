using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;

namespace CSharp_Bumblebee
{
    public partial class Form1
    {
        private const int DetectionConfirmConsecutiveHits = 2;
        private const int DetectionConfirmedMaxMisses = 2;

        // Box-only duplicate suppression. Keep this conservative so two real people
        // standing close together are not merged simply because their boxes overlap.
        private const double DetectionDuplicateStrongIou = 0.72;
        private const double DetectionDuplicateCenterRatio = 0.10;
        private const double DetectionDuplicateMinSizeRatio = 0.55;
        private const double DetectionTrackCenterRatio = 0.62;

        private readonly List<TemporalDetectionTrack> temporalDetectionTracks =
            new List<TemporalDetectionTrack>();

        private int nextTemporalDetectionTrackId = 1;
        private int rawDetectionPeopleCount;
        private int keptDetectionPeopleCount;

        private void ResetDetectionTemporalFilter()
        {
            temporalDetectionTracks.Clear();
            nextTemporalDetectionTrackId = 1;
            Volatile.Write(ref rawDetectionPeopleCount, 0);
            Volatile.Write(ref keptDetectionPeopleCount, 0);
        }

        private List<PersonDetection> UpdateDetectionTemporalFilter(
            List<PersonDetection> detections,
            int imageWidth,
            int imageHeight)
        {
            int rawCount = detections != null ? detections.Count : 0;
            Volatile.Write(ref rawDetectionPeopleCount, rawCount);

            List<PersonDetection> validDetections = new List<PersonDetection>();
            if (detections != null)
            {
                foreach (PersonDetection detection in detections)
                {
                    if (IsValidDetectionBox(detection, imageWidth, imageHeight))
                        validDetections.Add(detection);
                }
            }

            List<PersonDetection> deduplicated = SuppressDuplicateDetections(
                validDetections);

            Volatile.Write(ref keptDetectionPeopleCount, deduplicated.Count);

            foreach (TemporalDetectionTrack track in temporalDetectionTracks)
            {
                track.MatchedThisUpdate = false;
                track.Misses++;
            }

            foreach (PersonDetection detection in deduplicated)
            {
                TemporalDetectionTrack track =
                    FindBestTemporalDetectionTrack(detection);

                if (track == null)
                {
                    temporalDetectionTracks.Add(new TemporalDetectionTrack
                    {
                        Id = nextTemporalDetectionTrackId++,
                        LastDetection = detection,
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
                track.LastDetection = detection;
                track.Misses = 0;
                track.MatchedThisUpdate = true;

                if (!track.Confirmed &&
                    track.ConsecutiveHits >= DetectionConfirmConsecutiveHits)
                {
                    track.Confirmed = true;
                }
            }

            temporalDetectionTracks.RemoveAll(track =>
                (!track.Confirmed && track.Misses > 0) ||
                (track.Confirmed && track.Misses > DetectionConfirmedMaxMisses));

            MergeDuplicateTemporalDetectionTracks();

            List<PersonDetection> confirmedDetections =
                new List<PersonDetection>();

            foreach (TemporalDetectionTrack track in temporalDetectionTracks)
            {
                if (track.Confirmed && track.LastDetection != null)
                    confirmedDetections.Add(track.LastDetection);
            }

            return confirmedDetections;
        }

        private bool IsValidDetectionBox(
            PersonDetection detection,
            int imageWidth,
            int imageHeight)
        {
            if (detection == null || detection.Box.IsEmpty)
                return false;

            Rectangle box = detection.Box;
            if (box.Width < 12 || box.Height < 20)
                return false;

            return box.Right > 0 &&
                   box.Bottom > 0 &&
                   box.Left < imageWidth &&
                   box.Top < imageHeight;
        }

        private List<PersonDetection> SuppressDuplicateDetections(
            List<PersonDetection> detections)
        {
            List<PersonDetection> kept = new List<PersonDetection>();
            if (detections == null)
                return kept;

            foreach (PersonDetection candidate in detections)
            {
                int duplicateIndex = -1;

                for (int i = 0; i < kept.Count; i++)
                {
                    if (AreLikelyDuplicateBoxes(
                        kept[i].Box,
                        candidate.Box,
                        false))
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

                PersonDetection existing = kept[duplicateIndex];

                // Prefer confidence first. If confidence is essentially tied, keep
                // the larger body extent for a more stable disparity sample region.
                if (candidate.Confidence > existing.Confidence + 0.01f)
                {
                    kept[duplicateIndex] = candidate;
                    continue;
                }

                if (Math.Abs(candidate.Confidence - existing.Confidence) <= 0.01f)
                {
                    double existingArea =
                        (double)existing.Box.Width * existing.Box.Height;
                    double candidateArea =
                        (double)candidate.Box.Width * candidate.Box.Height;

                    if (candidateArea > existingArea)
                        kept[duplicateIndex] = candidate;
                }
            }

            return kept;
        }

        private TemporalDetectionTrack FindBestTemporalDetectionTrack(
            PersonDetection detection)
        {
            TemporalDetectionTrack bestTrack = null;
            double bestScore = double.NegativeInfinity;
            Point detectionCenter = GetBoxCenter(detection.Box);

            foreach (TemporalDetectionTrack track in temporalDetectionTracks)
            {
                if (track.MatchedThisUpdate || track.LastDetection == null)
                    continue;

                Rectangle previousBox = track.LastDetection.Box;
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

        private void MergeDuplicateTemporalDetectionTracks()
        {
            bool merged;

            do
            {
                merged = false;

                for (int i = 0;
                     i < temporalDetectionTracks.Count && !merged;
                     i++)
                {
                    TemporalDetectionTrack a = temporalDetectionTracks[i];
                    if (a.LastDetection == null)
                        continue;

                    for (int j = i + 1; j < temporalDetectionTracks.Count; j++)
                    {
                        TemporalDetectionTrack b = temporalDetectionTracks[j];
                        if (b.LastDetection == null)
                            continue;

                        if (!AreLikelyDuplicateBoxes(
                            a.LastDetection.Box,
                            b.LastDetection.Box,
                            true))
                        {
                            continue;
                        }

                        TemporalDetectionTrack survivor;
                        TemporalDetectionTrack loser;

                        if (a.Confirmed != b.Confirmed)
                        {
                            survivor = a.Confirmed ? a : b;
                            loser = a.Confirmed ? b : a;
                        }
                        else
                        {
                            // Preserve the older identity so exhibition colors remain
                            // stable if one person temporarily gets two detections.
                            survivor = a.Id <= b.Id ? a : b;
                            loser = a.Id <= b.Id ? b : a;
                        }

                        if (loser.MatchedThisUpdate && !survivor.MatchedThisUpdate)
                        {
                            survivor.LastDetection = loser.LastDetection;
                        }
                        else if (loser.MatchedThisUpdate == survivor.MatchedThisUpdate &&
                                 loser.LastDetection.Confidence >
                                 survivor.LastDetection.Confidence)
                        {
                            survivor.LastDetection = loser.LastDetection;
                        }

                        survivor.Confirmed = survivor.Confirmed || loser.Confirmed;
                        survivor.ConsecutiveHits = Math.Max(
                            survivor.ConsecutiveHits,
                            loser.ConsecutiveHits);
                        survivor.Misses = Math.Min(
                            survivor.Misses,
                            loser.Misses);
                        survivor.MatchedThisUpdate =
                            survivor.MatchedThisUpdate || loser.MatchedThisUpdate;

                        temporalDetectionTracks.Remove(loser);
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

        private int GetRawDetectionPeopleCount()
        {
            return Volatile.Read(ref rawDetectionPeopleCount);
        }

        private int GetKeptDetectionPeopleCount()
        {
            return Volatile.Read(ref keptDetectionPeopleCount);
        }
    }

    internal sealed class TemporalDetectionTrack
    {
        public int Id;
        public PersonDetection LastDetection;
        public int ConsecutiveHits;
        public int Misses;
        public bool Confirmed;
        public bool MatchedThisUpdate;
    }
}
