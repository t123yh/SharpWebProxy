using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;

namespace SharpWebProxy.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
            
        }
        
        public DbSet<Domain> Domains { get; set; }
        
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Domain>()
                .HasIndex(b => b.Code).IsUnique();
            modelBuilder.Entity<Domain>()
                .HasIndex(b => b.Name).IsUnique();
        }
    }
}