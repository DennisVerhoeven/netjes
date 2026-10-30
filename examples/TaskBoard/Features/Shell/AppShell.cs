using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TaskBoard.Data;
using TaskBoard.Infrastructure;

namespace TaskBoard.Features.Shell;

/// <summary>
/// The signed-in part of the app, app/(app)/layout.tsx: its loader lists the user's projects for the sidebar, and its
/// RequireAuthorization() covers every page below it before any loader runs.
/// </summary>
public static class AppShell
{
    public sealed record SidebarProject(string Slug, string Name, int OpenTasks);

    public sealed record ShellProps(IReadOnlyList<SidebarProject> Projects);

    public static void MapAppShell(this IEndpointRouteBuilder app) =>
        // By folder: (app) and (marketing) both sit at "/".
        app.MapLayoutLoader("app/(app)", async (ClaimsPrincipal user, TaskBoardDb db, CancellationToken cancellationToken) =>
            {
                var userId = user.UserId();
                var projects = await db.Memberships
                    .Where(m => m.UserId == userId)
                    .OrderBy(m => m.Project.Name)
                    .Select(m => new SidebarProject(m.Project.Slug, m.Project.Name, m.Project.Tasks.Count(t => t.State != TaskState.Done)))
                    .ToListAsync(cancellationToken);
                return Page.Props(new ShellProps(projects));
            })
            .RequireAuthorization();
}
