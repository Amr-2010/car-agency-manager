using Microsoft.EntityFrameworkCore;
namespace CarAgency.Api;
public class CarDb(DbContextOptions<CarDb> options):DbContext(options) {
 public DbSet<Agency> Agencies=>Set<Agency>(); public DbSet<Branch> Branches=>Set<Branch>(); public DbSet<Vehicle> Vehicles=>Set<Vehicle>(); public DbSet<Customer> Customers=>Set<Customer>(); public DbSet<Sale> Sales=>Set<Sale>(); public DbSet<Payment> Payments=>Set<Payment>(); public DbSet<Expense> Expenses=>Set<Expense>(); public DbSet<AppUser> Users=>Set<AppUser>();
 protected override void OnModelCreating(ModelBuilder b) { b.Entity<Vehicle>().HasIndex(x=>new{x.AgencyId,x.Vin}).IsUnique(); b.Entity<AppUser>().HasIndex(x=>x.Username).IsUnique(); b.Entity<Vehicle>().Property(x=>x.Price).HasPrecision(18,2); b.Entity<Vehicle>().Property(x=>x.Cost).HasPrecision(18,2); b.Entity<Sale>().Property(x=>x.Total).HasPrecision(18,2); b.Entity<Payment>().Property(x=>x.Amount).HasPrecision(18,2); b.Entity<Expense>().Property(x=>x.Amount).HasPrecision(18,2); }
}
