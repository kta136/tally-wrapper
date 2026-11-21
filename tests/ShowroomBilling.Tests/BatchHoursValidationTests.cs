using ShowroomBilling.Application;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Tests;

/// <summary>
/// What Settings → Admin may save as the shop hours of a batch: an opening and a closing time
/// together or neither, closing after opening on the same day, a weekday as the closed day, and a
/// gap of 0 to <see cref="BatchHoursLimits.MaxGapMinutes"/> minutes. Each refusal is worded for the
/// operator.
/// </summary>
public sealed class BatchHoursValidationTests
{
    [Fact]
    public void Hours_with_nothing_set_are_accepted()
    {
        Assert.Equal(new BatchHoursSettings(), Validate(new BatchHoursSettings()));
    }

    [Fact]
    public void Hours_are_accepted_at_their_limits_and_keep_hours_and_minutes_only()
    {
        var hours = new BatchHoursSettings(
            new TimeOnly(0, 0, 30), new TimeOnly(23, 59, 59, 999), DayOfWeek.Saturday, BatchHoursLimits.MaxGapMinutes);

        Assert.Equal(
            new BatchHoursSettings(new TimeOnly(0, 0), new TimeOnly(23, 59), DayOfWeek.Saturday, BatchHoursLimits.MaxGapMinutes),
            Validate(hours));
    }

    [Fact]
    public void A_closed_day_or_a_gap_needs_no_hours()
    {
        var hours = new BatchHoursSettings(ClosedOn: DayOfWeek.Sunday, GapMinutes: 10);

        Assert.Equal(hours, Validate(hours));
    }

    [Fact]
    public void One_time_without_the_other_is_refused()
    {
        const string message = "Give both an opening time and a closing time, or leave both empty.";

        Assert.Equal(message, Refused(new BatchHoursSettings(Opens: new TimeOnly(10, 0))));
        Assert.Equal(message, Refused(new BatchHoursSettings(Closes: new TimeOnly(20, 0))));
    }

    [Fact]
    public void Closing_must_be_later_than_opening_on_the_same_day()
    {
        const string message = "Closing time must be later than opening time, on the same day.";

        // No overnight hours.
        Assert.Equal(message, Refused(new BatchHoursSettings(new TimeOnly(20, 0), new TimeOnly(2, 0))));
        Assert.Equal(message, Refused(new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(10, 0))));
        // The same minute once the seconds are dropped.
        Assert.Equal(message, Refused(new BatchHoursSettings(new TimeOnly(10, 0, 5), new TimeOnly(10, 0, 50))));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(121)]
    [InlineData(int.MaxValue)]
    public void A_gap_outside_its_range_is_refused(int gapMinutes)
    {
        Assert.Equal("The gap between bills must be from 0 to 120 minutes.", Refused(new BatchHoursSettings(GapMinutes: gapMinutes)));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(-1)]
    public void A_closed_day_that_is_no_weekday_is_refused(int day)
    {
        Assert.Equal("The closed day must be a day of the week.", Refused(new BatchHoursSettings(ClosedOn: (DayOfWeek)day)));
    }

    [Fact]
    public void A_request_with_no_hours_is_refused()
    {
        var refused = Assert.Throws<RequestValidationException>(() =>
            CloudSettingsValidator.ValidateBatchHours(new UpdateBatchHoursRequest(null!)));

        Assert.Equal("Shop hours are required.", refused.Message);
    }

    private static BatchHoursSettings Validate(BatchHoursSettings hours)
        => CloudSettingsValidator.ValidateBatchHours(new UpdateBatchHoursRequest(hours));

    private static string Refused(BatchHoursSettings hours)
        => Assert.Throws<RequestValidationException>(() => Validate(hours)).Message;
}
