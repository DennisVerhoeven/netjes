using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskBoard.Data;
using TaskBoard.Infrastructure;

namespace TaskBoard.Features.Board;

/// <summary>
/// The board, app/(app)/projects/[id]/page.tsx: tasks by state, filtered by the search params (?q, ?priority,
/// ?mine), the Done column paged (?done=2). Adding and moving tasks are server actions that revalidate the board.
/// </summary>
public static class BoardEndpoints
{
    public const int DonePageSize = 10;

    public sealed record Assignee(Guid Id, string Name);

    public sealed record TaskCard(int Number, string Title, TaskPriority Priority, Assignee? Assignee, int Comments);

    public sealed record Column(TaskState State, string Label, IReadOnlyList<TaskCard> Tasks, int Total);

    public sealed record BoardProps(IReadOnlyList<Column> Columns, int DonePage, bool DoneHasMore);

    public sealed record CreateTaskInput
    {
        [Required] public string ProjectSlug { get; init; } = "";

        [Required(ErrorMessage = "Give the task a title."), StringLength(200)]
        public string Title { get; init; } = "";

        public string? Priority { get; init; }
        public string? State { get; init; }
    }

    public sealed record CreatedTask(int Number);

    public sealed record MoveTaskInput
    {
        [Required] public string ProjectSlug { get; init; } = "";
        public int Number { get; init; }
        [Required] public string State { get; init; } = "";
    }

    public static void MapBoard(this IEndpointRouteBuilder app)
    {
        app.MapPageLoader("/projects/[id]", async (
            string id, string? q, string? priority, bool? mine, int? done, ClaimsPrincipal user, TaskBoardDb db, CancellationToken cancellationToken) =>
        {
            // The layout's loader refuses non-members too, but it runs after this one: check here as well.
            var userId = user.UserId();
            var access = await db.AccessAsync(id, userId, cancellationToken);
            if (access is null)
            {
                return Page.NotFound();
            }

            if (!access.IsMember)
            {
                return Page.Forbidden();
            }

            var tasks = db.Tasks.Where(t => t.ProjectId == access.ProjectId);
            if (!string.IsNullOrWhiteSpace(q))
            {
                tasks = tasks.Where(t => EF.Functions.ILike(t.Title, $"%{EscapeLike(q.Trim())}%", "\\"));
            }

            if (TaskStates.TryParse(priority, out TaskPriority wanted))
            {
                tasks = tasks.Where(t => t.Priority == wanted);
            }

            if (mine == true)
            {
                tasks = tasks.Where(t => t.AssigneeId == userId);
            }

            var donePage = Math.Max(1, done ?? 1);
            var columns = new List<Column>();
            foreach (var state in TaskStates.All)
            {
                var inState = tasks.Where(t => t.State == state);
                var ordered = state == TaskState.Done
                    ? inState.OrderByDescending(t => t.UpdatedAt).Skip((donePage - 1) * DonePageSize).Take(DonePageSize)
                    : inState.OrderByDescending(t => t.Priority).ThenBy(t => t.Number);
                var cards = await ordered
                    .Select(t => new TaskCard(
                        t.Number,
                        t.Title,
                        t.Priority,
                        t.Assignee == null ? null : new Assignee(t.Assignee.Id, t.Assignee.Name),
                        t.Comments.Count))
                    .ToListAsync(cancellationToken);
                var total = await inState.CountAsync(cancellationToken);
                columns.Add(new Column(state, TaskStates.Label(state), cards, total));
            }

            var doneTotal = columns.Single(c => c.State == TaskState.Done).Total;
            return Page.Props(new BoardProps(columns, donePage, donePage * DonePageSize < doneTotal))
                .WithMetadata(new PageMetadata { Title = access.Name });
        });

        app.MapAction("tasks.create", async (
                [FromForm] CreateTaskInput input, ClaimsPrincipal user, TaskBoardDb db, TimeProvider time, CancellationToken cancellationToken) =>
            {
                var userId = user.UserId();
                if (await db.AccessAsync(input.ProjectSlug, userId, cancellationToken) is not { IsMember: true } access)
                {
                    return ServerAction.Forbidden();
                }

                var state = TaskStates.TryParse(input.State, out TaskState parsedState) ? parsedState : TaskState.Todo;
                var priority = TaskStates.TryParse(input.Priority, out TaskPriority parsedPriority) ? parsedPriority : TaskPriority.None;
                var number = await AddTaskAsync(db, access, userId, input.Title.Trim(), state, priority, time.GetUtcNow(), cancellationToken);
                return ServerAction.Ok(new CreatedTask(number))
                    .RevalidatePath($"/projects/{access.Slug}")
                    .Invalidate(ProjectLayout.StatsKey(access.Slug));
            })
            .RequireAuthorization();

        app.MapAction("tasks.move", async (
                [FromForm] MoveTaskInput input, ClaimsPrincipal user, TaskBoardDb db, TimeProvider time, CancellationToken cancellationToken) =>
            {
                var userId = user.UserId();
                if (await db.AccessAsync(input.ProjectSlug, userId, cancellationToken) is not { IsMember: true } access)
                {
                    return ServerAction.Forbidden();
                }

                if (!TaskStates.TryParse(input.State, out TaskState state))
                {
                    return ServerAction.Invalid("state", "Unknown state.");
                }

                var task = await db.Tasks.SingleOrDefaultAsync(t => t.ProjectId == access.ProjectId && t.Number == input.Number, cancellationToken);
                if (task is null)
                {
                    return ServerAction.Invalid("number", "There is no such task.");
                }

                if (task.State != state)
                {
                    var now = time.GetUtcNow();
                    task.State = state;
                    task.UpdatedAt = now;
                    db.LogActivity(access.ProjectId, userId, task.Number, $"moved {access.Slug.ToUpperInvariant()}-{task.Number} to {TaskStates.Label(state)}", now);
                    await db.SaveChangesAsync(cancellationToken);
                }

                return ServerAction.Ok()
                    .RevalidatePath($"/projects/{access.Slug}", $"/projects/{access.Slug}/tasks/{task.Number}")
                    .Invalidate(ProjectLayout.StatsKey(access.Slug));
            })
            .RequireAuthorization();
    }

    /// <summary>
    /// Takes the project's next task number with one atomic UPDATE ... RETURNING, in the same transaction as the task
    /// insert: two people adding a task at the same time queue on the project row and get consecutive numbers, instead
    /// of racing to the unique (project_id, number) index.
    /// </summary>
    private static async Task<int> AddTaskAsync(
        TaskBoardDb db, ProjectAccess access, Guid userId, string title, TaskState state, TaskPriority priority, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Not composable SQL: materialize with ToListAsync rather than composing SingleAsync over it.
        var number = (await db.Database
            .SqlQuery<int>($"UPDATE projects SET next_task_number = next_task_number + 1 WHERE id = {access.ProjectId} RETURNING next_task_number - 1 AS \"Value\"")
            .ToListAsync(cancellationToken)).Single();
        db.Tasks.Add(new TaskItem
        {
            ProjectId = access.ProjectId,
            Number = number,
            Title = title,
            State = state,
            Priority = priority,
            CreatedById = userId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.LogActivity(access.ProjectId, userId, number, $"created {access.Slug.ToUpperInvariant()}-{number}", now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return number;
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
