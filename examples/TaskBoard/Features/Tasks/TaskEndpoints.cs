using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskBoard.Data;
using TaskBoard.Features.Board;
using TaskBoard.Infrastructure;

namespace TaskBoard.Features.Tasks;

/// <summary>
/// A task, app/(app)/projects/[id]/tasks/[taskId]/page.tsx: the editable task, its comments, and the activity feed,
/// which streams in after the page (Page.Defer). Editing and commenting are server actions.
/// </summary>
public static class TaskEndpoints
{
    public sealed record Person(Guid Id, string Name);

    public sealed record TaskDetail(
        int Number, string Title, string Description, TaskState State, TaskPriority Priority, Person? Assignee, Person CreatedBy,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    public sealed record CommentItem(Guid Id, string Body, Person Author, DateTimeOffset CreatedAt);

    public sealed record ActivityItem(long Id, string Summary, string Actor, DateTimeOffset At);

    public sealed record TaskPageProps(
        TaskDetail Task, IReadOnlyList<Person> Members, IReadOnlyList<CommentItem> Comments, Deferred<IReadOnlyList<ActivityItem>> Activity);

    public sealed record UpdateTaskInput
    {
        [Required] public string ProjectSlug { get; init; } = "";
        public int Number { get; init; }

        [Required(ErrorMessage = "Give the task a title."), StringLength(200)]
        public string Title { get; init; } = "";

        [StringLength(10_000)] public string? Description { get; init; }
        [Required] public string State { get; init; } = "";
        [Required] public string Priority { get; init; } = "";
        public string? AssigneeId { get; init; }
    }

    public sealed record AddCommentInput
    {
        [Required] public string ProjectSlug { get; init; } = "";
        public int Number { get; init; }

        [Required(ErrorMessage = "Write something first."), StringLength(4_000)]
        public string Body { get; init; } = "";
    }

    public static void MapTasks(this IEndpointRouteBuilder app)
    {
        app.MapPageLoader("/projects/[id]/tasks/[taskId]", async (
            string id, string taskId, ClaimsPrincipal user, TaskBoardDb db, IDbContextFactory<TaskBoardDb> contexts, HttpContext http) =>
        {
            var access = await db.AccessAsync(id, user.UserId(), http.RequestAborted);
            if (access is null)
            {
                return Page.NotFound();
            }

            if (!access.IsMember)
            {
                return Page.Forbidden();
            }

            if (!int.TryParse(taskId, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || await LoadAsync(db, access.ProjectId, number, http.RequestAborted) is not { } task)
            {
                // Rendered by projects/[id]/not-found.tsx, inside the project layout.
                return Page.NotFound();
            }

            var comments = await db.Comments
                .Where(c => c.Task.ProjectId == access.ProjectId && c.Task.Number == number)
                .OrderBy(c => c.CreatedAt)
                .Select(c => new CommentItem(c.Id, c.Body, new Person(c.AuthorId, c.Author.Name), c.CreatedAt))
                .ToListAsync(http.RequestAborted);
            var members = await db.Memberships
                .Where(m => m.ProjectId == access.ProjectId)
                .OrderBy(m => m.User.Name)
                .Select(m => new Person(m.UserId, m.User.Name))
                .ToListAsync(http.RequestAborted);
            var activity = Page.Defer(ActivityAsync(contexts, access.ProjectId, number, http.RequestAborted));
            return Page.Props(new TaskPageProps(task, members, comments, activity))
                .WithMetadata(new PageMetadata { Title = $"{access.Slug.ToUpperInvariant()}-{number} {task.Title}" });
        });

        app.MapAction("tasks.update", async (
                [FromForm] UpdateTaskInput input, ClaimsPrincipal user, TaskBoardDb db, TimeProvider time, CancellationToken cancellationToken) =>
            {
                var userId = user.UserId();
                var access = await db.AccessAsync(input.ProjectSlug, userId, cancellationToken);
                if (access is not { IsMember: true })
                {
                    return access is null ? ServerAction.NotFound() : ServerAction.Forbidden();
                }

                var errors = new Dictionary<string, string[]>();
                if (!TaskStates.TryParse(input.State, out TaskState state)) errors["state"] = ["Pick a state."];
                if (!TaskStates.TryParse(input.Priority, out TaskPriority priority)) errors["priority"] = ["Pick a priority."];
                Guid? assigneeId = null;
                if (!string.IsNullOrEmpty(input.AssigneeId))
                {
                    if (Guid.TryParse(input.AssigneeId, out var candidate)
                        && await db.Memberships.AnyAsync(m => m.ProjectId == access.ProjectId && m.UserId == candidate, cancellationToken))
                    {
                        assigneeId = candidate;
                    }
                    else
                    {
                        errors["assigneeId"] = ["Only members of the project can be assigned."];
                    }
                }

                if (errors.Count > 0)
                {
                    return ServerAction.Invalid(errors);
                }

                var task = await db.Tasks.SingleOrDefaultAsync(t => t.ProjectId == access.ProjectId && t.Number == input.Number, cancellationToken);
                if (task is null)
                {
                    return ServerAction.NotFound();
                }

                var changed = new List<string>();
                var title = input.Title.Trim();
                var description = input.Description?.Trim() ?? "";
                if (task.Title != title) changed.Add("the title");
                if (task.Description != description) changed.Add("the description");
                if (task.State != state) changed.Add($"the state to {TaskStates.Label(state)}");
                if (task.Priority != priority) changed.Add($"the priority to {TaskStates.Name(priority)}");
                if (task.AssigneeId != assigneeId) changed.Add("the assignee");
                if (changed.Count > 0)
                {
                    var now = time.GetUtcNow();
                    (task.Title, task.Description, task.State, task.Priority, task.AssigneeId, task.UpdatedAt) =
                        (title, description, state, priority, assigneeId, now);
                    db.LogActivity(access.ProjectId, userId, task.Number, $"updated {Join(changed)} of {access.Slug.ToUpperInvariant()}-{task.Number}", now);
                    await db.SaveChangesAsync(cancellationToken);
                }

                return ServerAction.Ok()
                    .RevalidatePath($"/projects/{access.Slug}/tasks/{task.Number}", $"/projects/{access.Slug}")
                    .Invalidate(ProjectLayout.StatsKey(access.Slug));
            })
            .RequireAuthorization();

        app.MapAction("comments.add", async (
                [FromForm] AddCommentInput input, ClaimsPrincipal user, TaskBoardDb db, TimeProvider time, CancellationToken cancellationToken) =>
            {
                var userId = user.UserId();
                var access = await db.AccessAsync(input.ProjectSlug, userId, cancellationToken);
                if (access is not { IsMember: true })
                {
                    return access is null ? ServerAction.NotFound() : ServerAction.Forbidden();
                }

                var taskId = await db.Tasks
                    .Where(t => t.ProjectId == access.ProjectId && t.Number == input.Number)
                    .Select(t => (Guid?)t.Id)
                    .SingleOrDefaultAsync(cancellationToken);
                if (taskId is null)
                {
                    return ServerAction.NotFound();
                }

                var now = time.GetUtcNow();
                db.Comments.Add(new Comment { TaskId = taskId.Value, AuthorId = userId, Body = input.Body.Trim(), CreatedAt = now });
                db.LogActivity(access.ProjectId, userId, input.Number, $"commented on {access.Slug.ToUpperInvariant()}-{input.Number}", now);
                await db.SaveChangesAsync(cancellationToken);
                return ServerAction.Ok().RevalidatePath($"/projects/{access.Slug}/tasks/{input.Number}");
            })
            .RequireAuthorization();
    }

    private static async Task<TaskDetail?> LoadAsync(TaskBoardDb db, Guid projectId, int number, CancellationToken cancellationToken)
    {
        var row = await db.Tasks
            .Where(t => t.ProjectId == projectId && t.Number == number)
            .Select(t => new
            {
                t.Number, t.Title, t.Description, t.State, t.Priority, t.CreatedAt, t.UpdatedAt,
                Assignee = t.Assignee == null ? null : new Person(t.Assignee.Id, t.Assignee.Name),
                CreatedBy = db.Users.Where(u => u.Id == t.CreatedById).Select(u => new Person(u.Id, u.Name)).Single(),
            })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null
            ? null
            : new TaskDetail(
                row.Number, row.Title, row.Description, row.State, row.Priority, row.Assignee, row.CreatedBy,
                row.CreatedAt, row.UpdatedAt);
    }

    /// <summary>Streams after the page: on its own DbContext, since the request's keeps serving the layouts meanwhile.</summary>
    private static async Task<IReadOnlyList<ActivityItem>> ActivityAsync(
        IDbContextFactory<TaskBoardDb> contexts, Guid projectId, int number, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Activity
            .Where(a => a.ProjectId == projectId && a.TaskNumber == number)
            .OrderByDescending(a => a.At)
            .ThenByDescending(a => a.Id)
            .Take(30)
            .Select(a => new ActivityItem(a.Id, a.Summary, a.Actor.Name, a.At))
            .ToListAsync(cancellationToken);
    }

    private static string Join(List<string> parts) =>
        parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts[..^1])} and {parts[^1]}";
}
