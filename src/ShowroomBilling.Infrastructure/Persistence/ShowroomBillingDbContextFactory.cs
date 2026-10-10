using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ShowroomBilling.Infrastructure.Persistence;

public sealed class ShowroomBillingDbContextFactory : IDesignTimeDbContextFactory<ShowroomBillingDbContext>
{
    public ShowroomBillingDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ShowroomBillingDbContext>();
        optionsBuilder.UseNpgsql(
            DependencyInjection.DefaultLocalPostgresConnectionString,
            npgsql => npgsql.SetPostgresVersion(18, 0));
        return new ShowroomBillingDbContext(optionsBuilder.Options);
    }
}
