using HddBackup.Core.Shrinking;
using Xunit;

namespace HddBackup.Core.Tests.Shrinking
{
    public class ImageFitTests
    {
        private const long GiB = 1L << 30;

        private static ShrinkPlan Shrinkable(long minimum) => new ShrinkPlan { MinimumImageBytes = minimum };

        private static ShrinkPlan Blocked()
        {
            var plan = new ShrinkPlan { MinimumImageBytes = GiB };
            plan.Blockers.Add(new ShrinkBlockerInfo(ShrinkBlocker.JournalNeedsRecovery));
            return plan;
        }

        [Fact]
        public void Raw_FitsByFileSize()
        {
            Assert.Equal(ImageFitResult.Fits, ImageFit.Evaluate(true, 8 * GiB, 8 * GiB, null, false, 8 * GiB));
        }

        [Fact]
        public void Raw_LargerFile_ButPartitionsFit()
        {
            // Unused space after the last partition does not need to be written.
            Assert.Equal(ImageFitResult.Fits, ImageFit.Evaluate(true, 16 * GiB, 7 * GiB, null, false, 8 * GiB));
        }

        [Fact]
        public void Raw_LargerWithUnknownPartitions_IsPending()
        {
            Assert.Equal(ImageFitResult.Pending, ImageFit.Evaluate(true, 16 * GiB, 0, null, false, 8 * GiB));
        }

        [Fact]
        public void Gzip_FitsByRequiredBytes()
        {
            Assert.Equal(ImageFitResult.Fits, ImageFit.Evaluate(false, 2 * GiB, 8 * GiB, null, false, 8 * GiB));
        }

        [Fact]
        public void Larger_AnalysisNotFinished_IsPending()
        {
            Assert.Equal(ImageFitResult.Pending, ImageFit.Evaluate(false, 2 * GiB, 16 * GiB, null, false, 8 * GiB));
        }

        [Fact]
        public void Larger_AnalysisFailed_CannotFit()
        {
            Assert.Equal(ImageFitResult.CannotFit, ImageFit.Evaluate(false, 2 * GiB, 16 * GiB, null, true, 8 * GiB));
        }

        [Fact]
        public void Larger_ShrinkableBelowDevice_ShrinkToFit()
        {
            Assert.Equal(ImageFitResult.ShrinkToFit, ImageFit.Evaluate(false, 2 * GiB, 16 * GiB, Shrinkable(3 * GiB), false, 8 * GiB));
        }

        [Fact]
        public void Larger_MinimumAboveDevice_CannotFit()
        {
            Assert.Equal(ImageFitResult.CannotFit, ImageFit.Evaluate(true, 16 * GiB, 16 * GiB, Shrinkable(9 * GiB), false, 8 * GiB));
        }

        [Fact]
        public void Larger_WithBlockers_CannotFit()
        {
            Assert.Equal(ImageFitResult.CannotFit, ImageFit.Evaluate(false, 2 * GiB, 16 * GiB, Blocked(), false, 8 * GiB));
        }
    }
}
