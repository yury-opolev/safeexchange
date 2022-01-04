﻿/// <summary>
/// SafeExchangeDbContext
/// </summary>

namespace SafeExchange.Core
{
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Core.Model;

    public class SafeExchangeDbContext : DbContext
    {
        public DbSet<User> Users { get; set; }

        public DbSet<ObjectMetadata> Objects { get; set; }

        public DbSet<SubjectPermissions> Permissions { get; set; }

        public DbSet<AccessRequest> AccessRequests { get; set; }

        public DbSet<GroupDictionaryItem> GroupDictionary { get; set; }

        public DbSet<NotificationSubscription> NotificationSubscriptions { get; set; }

        public SafeExchangeDbContext(DbContextOptions<SafeExchangeDbContext> options)
            : base(options)
        { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ObjectMetadata>()
                .ToContainer("ObjectMetadata")
                .HasNoDiscriminator()
                .HasPartitionKey(om => om.PartitionKey);

            modelBuilder.Entity<ObjectMetadata>(
                omb =>
                {
                    omb.OwnsMany(
                        om => om.Content,
                        cntb =>
                        {
                            cntb.Property(cnt => cnt.ContentName).IsRequired();
                            cntb.OwnsMany(
                                cnt => cnt.Chunks,
                                chnkb =>
                                {
                                    chnkb.Property(chnk => chnk.ChunkName).IsRequired();
                                });
                        });

                    omb.OwnsOne(
                        om => om.ExpirationMetadata,
                        exmb =>
                        {
                            exmb.Property(exm => exm.ExpireAfterRead).IsRequired();
                            exmb.Property(exm => exm.ScheduleExpiration).IsRequired();
                            exmb.Property(exm => exm.ExpireAt).IsRequired();
                        });

                    omb.Navigation(o => o.ExpirationMetadata).IsRequired();
                });

            modelBuilder.Entity<SubjectPermissions>()
                .HasKey(sp => new { sp.SecretName, sp.SubjectName });

            modelBuilder.Entity<SubjectPermissions>()
                .ToContainer("SubjectPermissions")
                .HasNoDiscriminator()
                .HasPartitionKey(sp => sp.PartitionKey);

            modelBuilder.Entity<AccessRequest>()
                .ToContainer("AccessRequests")
                .HasNoDiscriminator()
                .HasPartitionKey(ar => ar.PartitionKey);

            modelBuilder.Entity<AccessRequest>(
               arb =>
               {
                   arb.OwnsMany(
                       ar => ar.Recipients,
                       rrb =>
                       {
                           rrb.Property(rr => rr.SubjectName).IsRequired();
                       });
               });

            modelBuilder.Entity<GroupDictionaryItem>()
                .ToContainer("GroupDictionary")
                .HasNoDiscriminator()
                .HasPartitionKey(gd => gd.PartitionKey);

            modelBuilder.Entity<NotificationSubscription>()
                .ToContainer("NotificationSubscriptions")
                .HasNoDiscriminator()
                .HasPartitionKey(ns => ns.PartitionKey);

            modelBuilder.Entity<User>()
                .ToContainer("Users")
                .HasNoDiscriminator()
                .HasPartitionKey(u => u.PartitionKey);
        }
    }
}
