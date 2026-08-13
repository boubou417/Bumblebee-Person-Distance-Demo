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

        // Duplicate suppression is intentionally stricter than normal tracking.
        // Two nearby real people may have overlapping boxes, but duplicate detections
        // of the same person also place several corresponding keypoints almost on top
        // of one another. Requiring keypoint agreement avoids over-merging crowds.
        private const int PoseDuplicateMinComparableKeypoints = 4;
        private const int PoseDuplicateMinComparableCoreKeypoints = 2;
        private const double PoseDuplicateMeanKeypointRatio = 0.085;
        private const double PoseDuplicateMeanCoreRatio = 0.070;
        private const double PoseDuplicateCenterRatio = 0.20;
        private const double PoseDuplicateMinimumSizeRatio = 0.50;
        private const double PoseDuplicateStrongIou = 0.70;

        private static readonly int[] PoseCoreKeypoints = { 5, 6, 11, 12 };

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

            // NMS works only on bounding boxes. A single person can occasionally
            // survive NMS twice when one box is shifted or covers a different body
            // extent. Use pose geometry to collapse those duplicates before tracking.
            List<PosePerson> deduplicatedDetections = SuppressDuplicatePoseDetections(
                validDetections,
                imageWidth,
                imageHeight);

            Volatile.Write(
                ref structuredPosePeopleCount,
                deduplicatedDetections.Count);

            // Age every track first. A successful match below resets Misses to zero.
            foreach (TemporalPoseTrack track in temporalPoseTracks)
            {
                track.MatchedThisUpdate = false;
                track.Misses++;
            }

            foreach (PosePerson detection in deduplicatedDetections)
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

            // If an older version of the same person already became two temporal
            // tracks, collapse them here as a second line of defense. Prefer the
            // established/older track so downstream color and distance tracking do
            // not jump simply because a duplicate detection appeared.
            MergeDuplicateTemporalPoseTracks(imageWidth, imageHeight);

            List<PosePerson> confirmedPeople = new List<PosePerson>();
            foreach (TemporalPoseTrack track in temporalPoseTracks)
            {
                if (track.Confirmed && track.LastPose != null)
                    confirmedPeople.Add(track.LastPose);
            }

            return confirmedPeople;
        }

        private List<PosePerson> SuppressDuplicatePoseDetections(
            List<PosePerson> detections,
            int imageWidth,
            int imageHeight)
        {
            List<PosePerson> kept = new List<PosePerson>();
            if (detections == null || detections.Count == 0)
                return kept;

            foreach (PosePerson candidate in detections)
            {
                int duplicateIndex = -1;

                for (int i = 0; i < kept.Count; i++)
                {
                    if (AreLikelySamePosePerson(
                        kept[i],
                        candidate,
                        imageWidth,
                        imageHeight,
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

                PosePerson existing = kept[duplicateIndex];
                double existingQuality = GetPoseQualityScore(
                    existing,
                    imageWidth,
                    imageHeight);
                double candidateQuality = GetPoseQualityScore(
                    candidate,
                    imageWidth,
                    imageHeight);

                if (candidateQuality > existingQuality)
                    kept[duplicateIndex] = candidate;
            }

            return kept;
        }

        private void MergeDuplicateTemporalPoseTracks(
            int imageWidth,
            int imageHeight)
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

                        if (!AreLikelySamePosePerson(
                            a.LastPose,
                            b.LastPose,
                            imageWidth,
                            imageHeight,
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
                            survivor = a.Id <= b.Id ? a : b;
                            loser = a.Id <= b.Id ? b : a;
                        }

                        bool loserHasFresherPose =
                            loser.MatchedThisUpdate && !survivor.MatchedThisUpdate;

                        if (!loserHasFresherPose &&
                            loser.MatchedThisUpdate == survivor.MatchedThisUpdate)
                        {
                            double survivorQuality = GetPoseQualityScore(
                                survivor.LastPose,
                                imageWidth,
                                imageHeight);
                            double loserQuality = GetPoseQualityScore(
                                loser.LastPose,
                                imageWidth,
                                imageHeight);
                            loserHasFresherPose = loserQuality > survivorQuality;
                        }

                        if (loserHasFresherPose)
                            survivor.LastPose = loser.LastPose;

                        survivor.Confirmed = survivor.Confirmed || loser.Confirmed;
                        survivor.ConsecutiveHits = Math.Max(
                            survivor.ConsecutiveHits,
                            loser.ConsecutiveHits);
                        survivor.Misses = Math.Min(survivor.Misses, loser.Misses);
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

        private bool AreLikelySamePosePerson(
            PosePerson a,
            PosePerson b,
            int imageWidth,
            int imageHeight,
            bool strict)
        {
            if (a == null || b == null ||
                a.Keypoints == null || b.Keypoints == null ||
                a.Box.IsEmpty || b.Box.IsEmpty)
            {
                return false;
            }

            double diagA = Math.Sqrt(
                (double)a.Box.Width * a.Box.Width +
                (double)a.Box.Height * a.Box.Height);
            double diagB = Math.Sqrt(
                (double)b.Box.Width * b.Box.Width +
                (double)b.Box.Height * b.Box.Height);
            double referenceDiagonal = Math.Max(40.0, Math.Max(diagA, diagB));

            Point centerA = GetBoxCenter(a.Box);
            Point centerB = GetBoxCenter(b.Box);
            double centerDx = centerA.X - centerB.X;
            double centerDy = centerA.Y - centerB.Y;
            double centerDistance = Math.Sqrt(
                centerDx * centerDx + centerDy * centerDy);

            double centerLimit = referenceDiagonal *
                (strict ? PoseDuplicateCenterRatio * 0.85 : PoseDuplicateCenterRatio);

            if (centerDistance > centerLimit)
                return false;

            double areaA = Math.Max(1.0, (double)a.Box.Width * a.Box.Height);
            double areaB = Math.Max(1.0, (double)b.Box.Width * b.Box.Height);
            double sizeRatio = Math.Min(areaA, areaB) / Math.Max(areaA, areaB);
            double minimumSizeRatio = strict
                ? Math.Max(0.60, PoseDuplicateMinimumSizeRatio)
                : PoseDuplicateMinimumSizeRatio;

            int comparableCount;
            double meanKeypointDistance = GetMeanMatchingKeypointDistance(
                a,
                b,
                imageWidth,
                imageHeight,
                null,
                out comparableCount);

            int comparableCoreCount;
            double meanCoreDistance = GetMeanMatchingKeypointDistance(
                a,
                b,
                imageWidth,
                imageHeight,
                PoseCoreKeypoints,
                out comparableCoreCount);

            double iou = ComputeIntersectionOverUnion(a.Box, b.Box);
            double keypointLimit = referenceDiagonal *
                (strict
                    ? PoseDuplicateMeanKeypointRatio * 0.85
                    : PoseDuplicateMeanKeypointRatio);
            double coreLimit = referenceDiagonal *
                (strict
                    ? PoseDuplicateMeanCoreRatio * 0.85
                    : PoseDuplicateMeanCoreRatio);

            bool strongKeypointMatch =
                comparableCount >= PoseDuplicateMinComparableKeypoints &&
                meanKeypointDistance <= keypointLimit;

            bool strongCoreMatch =
                comparableCoreCount >= PoseDuplicateMinComparableCoreKeypoints &&
                meanCoreDistance <= coreLimit;

            // This is the normal duplicate case: two detections describe nearly the
            // same joints. Box size may differ because one prediction is more cropped.
            if (strongKeypointMatch && strongCoreMatch)
                return true;

            // Fallback for predictions with fewer valid joints. Require very strong
            // box overlap, similar size, and at least some matching pose geometry.
            bool strongBoxDuplicate =
                iou >= (strict ? 0.76 : PoseDuplicateStrongIou) &&
                sizeRatio >= minimumSizeRatio &&
                comparableCount >= 3 &&
                meanKeypointDistance <= referenceDiagonal * 0.12;

            return strongBoxDuplicate;
        }

        private double GetMeanMatchingKeypointDistance(
            PosePerson a,
            PosePerson b,
            int imageWidth,
            int imageHeight,
            int[] indices,
            out int comparableCount)
        {
            comparableCount = 0;
            double totalDistance = 0;

            if (a == null || b == null ||
                a.Keypoints == null || b.Keypoints == null)
            {
                return double.MaxValue;
            }

            if (indices == null)
            {
                int count = Math.Min(a.Keypoints.Length, b.Keypoints.Length);
                for (int i = 0; i < count; i++)
                {
                    AddComparableKeypointDistance(
                        a.Keypoints[i],
                        b.Keypoints[i],
                        imageWidth,
                        imageHeight,
                        ref comparableCount,
                        ref totalDistance);
                }
            }
            else
            {
                foreach (int index in indices)
                {
                    if (index < 0 ||
                        index >= a.Keypoints.Length ||
                        index >= b.Keypoints.Length)
                    {
                        continue;
                    }

                    AddComparableKeypointDistance(
                        a.Keypoints[index],
                        b.Keypoints[index],
                        imageWidth,
                        imageHeight,
                        ref comparableCount,
                        ref totalDistance);
                }
            }

            return comparableCount > 0
                ? totalDistance / comparableCount
                : double.MaxValue;
        }

        private void AddComparableKeypointDistance(
            PoseKeypoint a,
            PoseKeypoint b,
            int imageWidth,
            int imageHeight,
            ref int comparableCount,
            ref double totalDistance)
        {
            if (!IsValidKeypoint(a, imageWidth, imageHeight) ||
                !IsValidKeypoint(b, imageWidth, imageHeight))
            {
                return;
            }

            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            totalDistance += Math.Sqrt(dx * dx + dy * dy);
            comparableCount++;
        }

        private double GetPoseQualityScore(
            PosePerson person,
            int imageWidth,
            int imageHeight)
        {
            if (person == null || person.Keypoints == null)
                return 0;

            double score = 0;

            for (int i = 0; i < person.Keypoints.Length; i++)
            {
                PoseKeypoint keypoint = person.Keypoints[i];
                if (!IsValidKeypoint(keypoint, imageWidth, imageHeight))
                    continue;

                score += 10.0 + Math.Max(0.0, keypoint.Confidence) * 2.0;
            }

            foreach (int index in PoseCoreKeypoints)
            {
                if (index < person.Keypoints.Length &&
                    IsValidKeypoint(
                        person.Keypoints[index],
                        imageWidth,
                        imageHeight))
                {
                    score += 5.0;
                }
            }

            return score;
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
