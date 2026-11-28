using Microsoft.Extensions.Logging.Abstractions;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Infrastructure.Bills;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Tally;

namespace ShowroomBilling.Tests.TestSupport;

/// <summary>Real workflows sharing a test database and audit store, with a configurable Tally stand-in.</summary>
internal sealed class BillWorkflows
{
    public BillWorkflows(
        ShowroomBillingDbContext db,
        ITallyPoster? poster = null,
        ITallyCompanyHealthService? tallyHealth = null,
        TimeProvider? timeProvider = null,
        ITallyVoucherLookup? tallyVoucherLookup = null)
    {
        var numbering = new NumberingService(db);
        Audit = new BillAuditStore(db);
        Read = new BillReadWorkflow(db);
        Lifecycle = new BillLifecycleWorkflow(db, numbering, Audit);
        Admin = new BillAdminWorkflow(db, numbering, Audit);
        Posting = new BillPostingWorkflow(db, poster ?? new FakeTallyPoster(), Audit,
            NullLogger<BillPostingWorkflow>.Instance, tallyHealth, timeProvider, tallyVoucherLookup);
    }

    public BillReadWorkflow Read { get; }
    public BillLifecycleWorkflow Lifecycle { get; }
    public BillPostingWorkflow Posting { get; }
    public BillAdminWorkflow Admin { get; }
    public BillAuditStore Audit { get; }
}
