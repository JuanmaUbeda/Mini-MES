using MiniMes.Core;
using MiniMes.Core.Models;

namespace MiniMes.Tests;

public class BatchTests
{
    [Fact]
    public void NewBatch_StartsInCreatedStatus()
    {
        var batch = TestData.NewBatch();

        Assert.Equal(BatchStatus.Created, batch.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(500.01)]
    public void Constructor_RejectsInvalidQuantity(double quantity)
    {
        Assert.Throws<DomainException>(() => TestData.NewBatch((decimal)quantity));
    }

    [Fact]
    public void Constructor_AcceptsQuantityAtMixerCapacity()
    {
        var batch = TestData.NewBatch(Batch.MaxQuantityKg);

        Assert.Equal(Batch.MaxQuantityKg, batch.PlannedQuantityKg);
    }

    [Fact]
    public void Constructor_RejectsBlankBatchId()
    {
        Assert.Throws<DomainException>(() =>
            new Batch(" ", TestData.StandardRecipe(), 100, DateTime.UtcNow));
    }

    [Fact]
    public void HappyPath_CreatedToCompleted()
    {
        var batch = TestData.NewBatch();

        batch.MarkReady();
        batch.Start();
        batch.Complete();

        Assert.Equal(BatchStatus.Completed, batch.Status);
    }

    [Fact]
    public void Start_FromCreated_IsRejected()
    {
        var batch = TestData.NewBatch();

        Assert.Throws<DomainException>(() => batch.Start());
    }

    [Fact]
    public void Complete_FromReady_IsRejected()
    {
        var batch = TestData.NewBatch();
        batch.MarkReady();

        Assert.Throws<DomainException>(() => batch.Complete());
    }

    [Fact]
    public void Fail_FromCreated_SetsErrorAndReason()
    {
        var batch = TestData.NewBatch();

        batch.Fail("Rejected by ERP");

        Assert.Equal(BatchStatus.Error, batch.Status);
        Assert.Equal("Rejected by ERP", batch.ErrorMessage);
    }

    [Fact]
    public void Fail_FromCompleted_IsRejected()
    {
        var batch = TestData.NewBatch();
        batch.MarkReady();
        batch.Start();
        batch.Complete();

        Assert.Throws<DomainException>(() => batch.Fail("too late"));
    }

    [Fact]
    public void Fail_RequiresReason()
    {
        var batch = TestData.NewBatch();

        Assert.Throws<DomainException>(() => batch.Fail(""));
    }

    [Fact]
    public void ErrorBatch_CannotBeRestarted()
    {
        var batch = TestData.NewBatch();
        batch.Fail("boom");

        Assert.Throws<DomainException>(() => batch.MarkReady());
    }
}
