using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ShowroomBilling.Infrastructure.Persistence;

public sealed class ShowroomBillingDbContextFactory : IDesignTimeDbContextFactory<ShowroomBillingDbContext>
{
    public ShowroomBillingDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ShowroomBillingDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=tally_wrapper;Username=postgres;Password=postgres");
        return new ShowroomBillingDbContext(optionsBuilder.Options);
    }
}
