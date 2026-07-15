using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Tests;

public sealed class BillStateCapabilitiesTests
{
    public static IEnumerable<object[]> Matrix
    {
        get
        {
            yield return new object[] { BillStates.Draft, true, true, false, false, true, true, true, true, true, true, false, false };
            yield return new object[] { BillStates.Pending, true, true, false, false, true, true, true, true, true, true, false, false };
            yield return new object[] { BillStates.Posting, false, false, false, false, false, false, false, false, false, false, false, false };
            yield return new object[] { BillStates.Posted, false, false, false, true, true, false, true, true, true, false, true, true };
            yield return new object[] { BillStates.Failed, false, false, true, true, false, true, true, true, true, true, true, true };
            yield return new object[] { BillStates.ReconciliationRequired, false, false, false, false, false, false, false, false, false, true, true, true };
            yield return new object[] { BillStates.Revised, false, false, false, false, false, false, false, true, true, false, false, false };
            yield return new object[] { BillStates.Voided, false, false, false, false, false, false, false, true, true, false, false, false };
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Capabilities_MatchExpectedActionRules(
        string state,
        bool isPendingLike,
        bool canPush,
        bool canRetry,
        bool canRepost,
        bool canRevise,
        bool canVoid,
        bool canEdit,
        bool canChangeNumber,
        bool canDelete,
        bool canMarkPosted,
        bool canMarkPending,
        bool tallyDivergesIfDeleted)
    {
        Assert.True(BillStateCapabilities.IsKnown(state));
        Assert.Equal(isPendingLike, BillStateCapabilities.IsPendingLike(state));
        Assert.Equal(canPush, BillStateCapabilities.CanPush(state));
        Assert.Equal(canRetry, BillStateCapabilities.CanRetry(state));
        Assert.Equal(canRepost, BillStateCapabilities.CanRepost(state));
        Assert.Equal(canRevise, BillStateCapabilities.CanRevise(state));
        Assert.Equal(canVoid, BillStateCapabilities.CanVoid(state));
        Assert.Equal(canEdit, BillStateCapabilities.CanEdit(state));
        Assert.Equal(canChangeNumber, BillStateCapabilities.CanChangeNumber(state));
        Assert.Equal(canDelete, BillStateCapabilities.CanDelete(state));
        Assert.Equal(canMarkPosted, BillStateCapabilities.CanMarkPosted(state));
        Assert.Equal(canMarkPending, BillStateCapabilities.CanMarkPending(state));
        Assert.Equal(tallyDivergesIfDeleted, BillStateCapabilities.TallyDivergesIfDeleted(state));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("Pending")]
    public void Capabilities_RejectUnknownStates(string? state)
    {
        Assert.False(BillStateCapabilities.IsKnown(state));
        Assert.False(BillStateCapabilities.IsPendingLike(state));
        Assert.False(BillStateCapabilities.CanPush(state));
        Assert.False(BillStateCapabilities.CanRetry(state));
        Assert.False(BillStateCapabilities.CanRepost(state));
        Assert.False(BillStateCapabilities.CanRevise(state));
        Assert.False(BillStateCapabilities.CanVoid(state));
        Assert.False(BillStateCapabilities.CanEdit(state));
        Assert.False(BillStateCapabilities.CanChangeNumber(state));
        Assert.False(BillStateCapabilities.CanDelete(state));
        Assert.False(BillStateCapabilities.CanMarkPosted(state));
        Assert.False(BillStateCapabilities.CanMarkPending(state));
        Assert.False(BillStateCapabilities.TallyDivergesIfDeleted(state));
    }
}
