using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TaskBoard.Data;
using TaskBoard.Infrastructure;

namespace TaskBoard.Features.Board;

/// <summary>
/// app/(app)/projects/[id]/layout.tsx: the project header, tabs and live stats for every page of a project.
/// Non-members get Page.Forbidden(), rendered by the forbidden.tsx a level up (outside this layout), and unknown
/// projects Page.NotFound(). Also the stats API the header polls through TanStack Query.
/// </summary>
public static class ProjectLayout
{
    public sealed record ProjectSummary(string Slug, string Name, string Description, MemberRole Role);

    public sealed record Member(Guid Id, string Name);

    public sealed record ProjectLayoutProps(ProjectSummary Project, IReadOnlyList<Member> Members);

    public sealed record ProjectStats(int Total, int Backlog, int Todo, int InProgress, int Done);

    /// <summary>The TanStack Query key of a project's stats, also used in ProjectHeader.tsx.</summary>
    public static IReadOnlyList<object?> StatsKey(string slug) => ["projects", slug, "stats"];

    public static void MapProjectLayout(this IEndpointRouteBuilder app)
    {
        app.MapLayoutLoader("/projects/[id]", async (string id, ClaimsPrincipal user, TaskBoardDb db, CancellationToken cancellationToken) =>
        {
            var access = await db.AccessAsync(id, user.UserId(), cancellationToken);
            if (access is null)
            {
                return Page.NotFound();
            }

            if (!access.IsMember)
            {
                return Page.Forbidden();
            }

            var project = await db.Projects
                .Where(p => p.Id == access.ProjectId)
                .Select(p => new { p.Slug, p.Name, p.Description })
                .SingleAsync(cancellationToken);
            var members = await db.Memberships
                .Where(m => m.ProjectId == access.ProjectId)
                .OrderBy(m => m.User.Name)
                .Select(m => new Member(m.UserId, m.User.Name))
                .ToListAsync(cancellationToken);
            return Page.Props(new ProjectLayoutProps(
                    new ProjectSummary(project.Slug, project.Name, project.Description, access.Role!.Value),
                    members))
                .WithQuery(StatsKey(access.Slug), await StatsAsync(db, access.ProjectId, cancellationToken));
        });

        // Typed results, so the generated api['GET /api/projects/{slug}/stats'] knows what it returns.
        app.MapGet("/api/projects/{slug}/stats", async Task<Results<Ok<ProjectStats>, NotFound, ForbidHttpResult>> (
                string slug, ClaimsPrincipal user, TaskBoardDb db, CancellationToken cancellationToken) =>
            await db.AccessAsync(slug, user.UserId(), cancellationToken) switch
            {
                null => TypedResults.NotFound(),
                { IsMember: false } => TypedResults.Forbid(),
                var access => TypedResults.Ok(await StatsAsync(db, access.ProjectId, cancellationToken)),
            })
            .RequireAuthorization();
    }

    public static async Task<ProjectStats> StatsAsync(TaskBoardDb db, Guid projectId, CancellationToken cancellationToken)
    {
        var counts = await db.Tasks
            .Where(t => t.ProjectId == projectId)
            .GroupBy(t => t.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.State, g => g.Count, cancellationToken);
        int Of(TaskState state) => counts.GetValueOrDefault(state);
        return new ProjectStats(counts.Values.Sum(), Of(TaskState.Backlog), Of(TaskState.Todo), Of(TaskState.InProgress), Of(TaskState.Done));
    }
}
