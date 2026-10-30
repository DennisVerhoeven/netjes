using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TaskBoard.Data;

public sealed class TaskBoardDb(DbContextOptions<TaskBoardDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<ActivityEntry> Activity => Set<ActivityEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TaskBoardDb).Assembly);

    public static void Configure(DbContextOptionsBuilder options, string? connectionString) =>
        options
            .UseNpgsql(connectionString ?? throw new InvalidOperationException(
                "Set ConnectionStrings:TaskBoard (ConnectionStrings__TaskBoard). See examples/TaskBoard/README.md."))
            .UseSnakeCaseNamingConvention();
}

/// <summary>For <c>dotnet ef migrations add</c>: no connection is opened.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<TaskBoardDb>
{
    public TaskBoardDb CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TaskBoardDb>();
        TaskBoardDb.Configure(options, "Host=localhost;Database=taskboard_design");
        return new TaskBoardDb(options.Options);
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> user)
    {
        user.HasKey(u => u.Id);
        user.Property(u => u.Email).HasMaxLength(254);
        user.HasIndex(u => u.Email).IsUnique();
        user.Property(u => u.Name).HasMaxLength(80);
        user.Property(u => u.PasswordHash).HasMaxLength(200);
    }
}

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> project)
    {
        project.HasKey(p => p.Id);
        project.Property(p => p.Slug).HasMaxLength(24);
        project.HasIndex(p => p.Slug).IsUnique();
        project.Property(p => p.Name).HasMaxLength(80);
        project.Property(p => p.Description).HasMaxLength(500);
        // NextTaskNumber is bumped by an atomic UPDATE ... RETURNING (BoardEndpoints.AddTaskAsync), so two tasks never get
        // the same number. Still a concurrency token: any other write of the project row cannot overwrite a newer counter.
        project.Property(p => p.NextTaskNumber).IsConcurrencyToken();
    }
}

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> membership)
    {
        membership.HasKey(m => new { m.ProjectId, m.UserId });
        membership.Property(m => m.Role).HasConversion<string>().HasMaxLength(16);
        membership.HasOne(m => m.Project).WithMany(p => p.Members).HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Cascade);
        membership.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        membership.HasIndex(m => m.UserId);
    }
}

internal sealed class TaskConfiguration : IEntityTypeConfiguration<TaskItem>
{
    public void Configure(EntityTypeBuilder<TaskItem> task)
    {
        task.ToTable("tasks");
        task.HasKey(t => t.Id);
        task.HasIndex(t => new { t.ProjectId, t.Number }).IsUnique();
        task.HasIndex(t => new { t.ProjectId, t.State });
        task.Property(t => t.Title).HasMaxLength(200);
        task.Property(t => t.Description).HasMaxLength(10_000);
        task.Property(t => t.State).HasConversion<string>().HasMaxLength(16);
        task.Property(t => t.Priority).HasConversion<string>().HasMaxLength(16);
        task.HasOne(t => t.Project).WithMany(p => p.Tasks).HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Cascade);
        task.HasOne(t => t.Assignee).WithMany().HasForeignKey(t => t.AssigneeId).OnDelete(DeleteBehavior.SetNull);
        task.HasOne<User>().WithMany().HasForeignKey(t => t.CreatedById).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> comment)
    {
        comment.HasKey(c => c.Id);
        comment.Property(c => c.Body).HasMaxLength(4_000);
        comment.HasOne(c => c.Task).WithMany(t => t.Comments).HasForeignKey(c => c.TaskId).OnDelete(DeleteBehavior.Cascade);
        comment.HasOne(c => c.Author).WithMany().HasForeignKey(c => c.AuthorId).OnDelete(DeleteBehavior.Restrict);
        comment.HasIndex(c => new { c.TaskId, c.CreatedAt });
    }
}

internal sealed class ActivityConfiguration : IEntityTypeConfiguration<ActivityEntry>
{
    public void Configure(EntityTypeBuilder<ActivityEntry> activity)
    {
        activity.ToTable("activity");
        activity.HasKey(a => a.Id);
        activity.Property(a => a.Id).UseIdentityAlwaysColumn();
        activity.Property(a => a.Summary).HasMaxLength(300);
        activity.HasOne(a => a.Actor).WithMany().HasForeignKey(a => a.ActorId).OnDelete(DeleteBehavior.Restrict);
        activity.HasOne<Project>().WithMany().HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.Cascade);
        activity.HasIndex(a => new { a.ProjectId, a.TaskNumber, a.At });
    }
}
