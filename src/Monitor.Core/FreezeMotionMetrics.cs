using System;
using System.IO;

namespace Monitor.Core;

public sealed record FreezeMotionMetrics(
    double PreviousPixelDifference,
    double ReferencePixelDifference,
    double PreviousMovingAreaPercent,
    double ReferenceMovingAreaPercent,
    bool HasPrevious, bool HasReference);

public sealed class FreezeMotionAccumulator(double changedPixelPercent)
{
    double pixels, previousDifference, referenceDifference, previousMovingPixels, referenceMovingPixels;
    public void AddTile(double validPixels,
        double previousDifferenceSum,
        double referenceDifferenceSum,
        double previousChangedPixels,
        double referenceChangedPixels)
    {
        if (validPixels <= 0)
        {
            return;
        }
        pixels += validPixels;
        previousDifference += previousDifferenceSum;
        referenceDifference += referenceDifferenceSum;
        if (previousChangedPixels > 0 && previousChangedPixels * 100 + 1e-7 >= validPixels * changedPixelPercent)
        {
            previousMovingPixels += validPixels;
        }
        if (referenceChangedPixels > 0 && referenceChangedPixels * 100 + 1e-7 >= validPixels * changedPixelPercent)
        {
            referenceMovingPixels += validPixels;
        }
    }
    public FreezeMotionMetrics Finish(bool hasPrevious, bool hasReference) => new(
        pixels > 0 ? previousDifference / pixels * 255 : 0,
        pixels > 0 ? referenceDifference / pixels * 255 : 0,
        pixels > 0 ? previousMovingPixels / pixels * 100 : 0,
        pixels > 0 ? referenceMovingPixels / pixels * 100 : 0,
        hasPrevious && pixels > 0, hasReference && pixels > 0);
}
