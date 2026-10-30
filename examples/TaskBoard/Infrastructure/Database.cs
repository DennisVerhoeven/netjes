using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TaskBoard.Data;

namespace TaskBoard.Infrastructure;

/// <summary>
/// Migrations run as an explicit step in production (<c>dotnet TaskBoard.dll migrate</c>, the compose <c>migrate</c>
/// service), never on start, where replicas would race. Development migrates and seeds a demo account on start.
/// </summary>
public static class Database
{
    public const string DemoEmail = "demo@taskboard.test";
    public const string DemoPassword = "taskboard-demo";

    public static async Task MigrateAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TaskBoardDb>().Database.MigrateAsync();
    }

    public static async Task MigrateAndSeedAsync(IServiceProvider services)
    {
        await MigrateAsync(services);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TaskBoardDb>();
        if (await db.Users.AnyAsync(u => u.Email == DemoEmail))
        {
            return;
        }

        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        var demo = new User { Email = DemoEmail, Name = "Demo Dev", CreatedAt = now };
        demo.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(demo, DemoPassword);
        var project = new Project { Slug = "demo", Name = "Demo launch", Description = "Everything between here and the launch.", CreatedAt = now };
        project.Members.Add(new Membership { User = demo, Role = MemberRole.Owner, JoinedAt = now });
        (string Title, TaskState State, TaskPriority Priority)[] tasks =
        [
            ("Write the launch post", TaskState.InProgress, TaskPriority.High),
            ("Pick a domain name", TaskState.Done, TaskPriority.Medium),
            ("Set up Postgres backups", TaskState.Todo, TaskPriority.Urgent),
            ("Add dark mode", TaskState.Backlog, TaskPriority.Low),
            ("Invite the beta testers", TaskState.Todo, TaskPriority.Medium),
        ];
        foreach (var (title, state, priority) in tasks)
        {
            project.Tasks.Add(new TaskItem
            {
                Number = project.NextTaskNumber++,
                Title = title,
                State = state,
                Priority = priority,
                CreatedById = demo.Id,
                AssigneeId = state == TaskState.InProgress ? demo.Id : null,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        db.Projects.Add(project);
        await db.SaveChangesAsync();
    }
}
