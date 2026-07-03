using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Data;

public class KinshoutDbContext(DbContextOptions<KinshoutDbContext> options) : DbContext(options)
{
    public DbSet<ApiClient> ApiClients => Set<ApiClient>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserLogin> UserLogins => Set<UserLogin>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Advert> Adverts => Set<Advert>();
    public DbSet<Discussion> Discussions => Set<Discussion>();
    public DbSet<DiscussionReply> DiscussionReplies => Set<DiscussionReply>();
    public DbSet<SearchQueryStat> SearchQueryStats => Set<SearchQueryStat>();
    public DbSet<SavedAdvert> SavedAdverts => Set<SavedAdvert>();
    public DbSet<LikedDiscussion> LikedDiscussions => Set<LikedDiscussion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiClient>(e =>
        {
            e.HasIndex(x => x.ClientId).IsUnique();
            e.Property(x => x.ClientId).HasMaxLength(80);
            e.Property(x => x.Name).HasMaxLength(120);
        });

        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.Property(x => x.WhatsAppNumber).HasMaxLength(32);
            e.Property(x => x.DisplayPreference).HasMaxLength(16).HasDefaultValue(DisplayPreferenceMode.Clair);
            e.Property(x => x.IsProfilePublic).HasDefaultValue(true);
        });

        modelBuilder.Entity<UserLogin>(e =>
        {
            e.HasIndex(x => new { x.Provider, x.ProviderKey }).IsUnique();
            e.HasOne(x => x.User).WithMany(x => x.Logins).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Category>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(80);
            e.Property(x => x.Label).HasMaxLength(120);
            e.Property(x => x.Icon).HasMaxLength(16);
        });

        modelBuilder.Entity<Advert>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Price).HasMaxLength(64);
            e.Property(x => x.Location).HasMaxLength(120);
            e.HasIndex(x => new { x.IsPublished, x.CreatedAt });
            e.HasIndex(x => new { x.IsPublished, x.ViewCount, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.IsPublished, x.CreatedAt });
            e.HasIndex(x => new { x.CategoryId, x.IsPublished, x.CreatedAt });
            e.HasOne(x => x.User).WithMany(x => x.Adverts).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Category).WithMany(x => x.Adverts).HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Discussion>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.ReplyCount).HasDefaultValue(0);
            e.Property(x => x.LikeCount).HasDefaultValue(0);
            e.Property(x => x.ViewCount).HasDefaultValue(0);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.ReplyCount, x.CreatedAt });
            e.HasIndex(x => new { x.ViewCount, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.UpdatedAt });
            e.HasOne(x => x.User).WithMany(x => x.Discussions).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Category).WithMany(x => x.Discussions).HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DiscussionReply>(e =>
        {
            e.HasIndex(x => new { x.DiscussionId, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.DiscussionId });
            e.HasOne(x => x.Discussion).WithMany(x => x.Replies).HasForeignKey(x => x.DiscussionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany(x => x.Replies).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SearchQueryStat>(e =>
        {
            e.HasIndex(x => x.NormalizedQuery).IsUnique();
            e.Property(x => x.NormalizedQuery).HasMaxLength(200);
            e.Property(x => x.DisplayQuery).HasMaxLength(200);
        });

        modelBuilder.Entity<SavedAdvert>(e =>
        {
            e.HasKey(x => new { x.UserId, x.AdvertId });
            e.HasIndex(x => x.AdvertId);
            e.HasOne(x => x.User).WithMany(x => x.SavedAdverts).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Advert).WithMany().HasForeignKey(x => x.AdvertId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LikedDiscussion>(e =>
        {
            e.HasKey(x => new { x.UserId, x.DiscussionId });
            e.HasIndex(x => x.DiscussionId);
            e.HasOne(x => x.User).WithMany(x => x.LikedDiscussions).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Discussion).WithMany().HasForeignKey(x => x.DiscussionId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
