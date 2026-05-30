/// <summary>
/// SafeExchangeDbContext
/// </summary>

namespace SafeExchange.Core
{
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Core.Model;

    public class SafeExchangeDbContext : DbContext
    {
        public DbSet<User> Users { get; set; }

        public DbSet<Application> Applications { get; set; }

        public DbSet<ApplicationOwner> ApplicationOwners { get; set; }

        public DbSet<ObjectMetadata> Objects { get; set; }

        public DbSet<SubjectPermissions> Permissions { get; set; }

        public DbSet<AccessRequest> AccessRequests { get; set; }

        public DbSet<GroupDictionaryItem> GroupDictionary { get; set; }

        public DbSet<WebhookSubscription> WebhookSubscriptions { get; set; }

        public DbSet<WebhookNotificationData> WebhookNotificationData { get; set; }

        public DbSet<PinnedGroup> PinnedGroups { get; set; }

        public DbSet<PinnedSecret> PinnedSecrets { get; set; }

        public DbSet<SecretAuditAnchor> SecretAuditAnchors { get; set; }

        public DbSet<SecretAuditEvent> SecretAuditEvents { get; set; }

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
                            exmb.Property(exm => exm.ScheduleExpiration).IsRequired();
                            exmb.Property(exm => exm.ExpireAt).IsRequired();
                            exmb.Property(exm => exm.ExpireOnIdleTime).IsRequired();
                            exmb.Property(exm => exm.IdleTimeToExpire).IsRequired();
                        });

                    omb.Navigation(o => o.ExpirationMetadata).IsRequired();
                });

            modelBuilder.Entity<SubjectPermissions>()
                .HasKey(sp => new { sp.SecretName, sp.SubjectType, sp.SubjectId });

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
                           rrb.HasKey(rr => new { rr.AccessRequestId, rr.SubjectType, rr.SubjectId });
                           rrb.Property(rr => rr.AccessRequestId).IsRequired();
                           rrb.Property(rr => rr.SubjectType).IsRequired();
                           rrb.Property(rr => rr.SubjectId).IsRequired();
                       });
               });

            modelBuilder.Entity<GroupDictionaryItem>()
                .ToContainer("GroupDictionary")
                .HasNoDiscriminator()
                .HasPartitionKey(gd => gd.PartitionKey);

            modelBuilder.Entity<User>()
                .ToContainer("Users")
                .HasNoDiscriminator()
                .HasPartitionKey(u => u.PartitionKey);

            modelBuilder.Entity<Application>()
                .ToContainer("Applications")
                .HasNoDiscriminator()
                .HasPartitionKey(a => a.PartitionKey);

            modelBuilder.Entity<ApplicationOwner>()
                .ToContainer("ApplicationOwners")
                .HasNoDiscriminator()
                .HasPartitionKey(ao => ao.PartitionKey);

            // Composite key: an owner is uniquely identified by the app it owns + the
            // principal (typed). Same SubjectId can be both a User and a Group owner
            // of different apps, so the type is part of the key.
            modelBuilder.Entity<ApplicationOwner>()
                .HasKey(ao => new { ao.ApplicationId, ao.SubjectType, ao.SubjectId });

            modelBuilder.Entity<WebhookSubscription>()
                .ToContainer("WebhookSubscriptions")
                .HasNoDiscriminator()
                .HasPartitionKey(ws => ws.PartitionKey);

            modelBuilder.Entity<WebhookNotificationData>()
                .ToContainer("WebhookNotificationData")
                .HasNoDiscriminator()
                .HasPartitionKey(wnd => wnd.PartitionKey);

            modelBuilder.Entity<PinnedGroup>()
                .ToContainer("PinnedGroups")
                .HasNoDiscriminator()
                .HasPartitionKey(pg => pg.PartitionKey);

            modelBuilder.Entity<PinnedGroup>()
                .HasKey(pg => new { pg.UserId, pg.GroupItemId });

            modelBuilder.Entity<PinnedSecret>()
                .ToContainer("PinnedSecrets")
                .HasNoDiscriminator()
                .HasPartitionKey(ps => ps.PartitionKey);

            modelBuilder.Entity<PinnedSecret>()
                .HasKey(ps => new { ps.UserId, ps.SecretName });

            modelBuilder.Entity<SecretAuditAnchor>()
                .ToContainer("SecretAuditAnchors")
                .HasNoDiscriminator()
                .HasPartitionKey(a => a.AuditInstanceId);
            modelBuilder.Entity<SecretAuditAnchor>().HasKey(a => a.id);

            modelBuilder.Entity<SecretAuditEvent>()
                .ToContainer("SecretAuditEvents")
                .HasNoDiscriminator()
                .HasPartitionKey(e => e.AuditInstanceId);
            modelBuilder.Entity<SecretAuditEvent>().HasKey(e => e.id);
        }
    }
}
