using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskBoard.Data;
using TaskBoard.Infrastructure;

namespace TaskBoard.Features.Projects;

/// <summary>/projects: the user's projects, and creating one (app/(app)/projects/page.tsx).</summary>
public static class ProjectsEndpoints
{
    public sealed record ProjectCard(string Slug, string Name, string Description, MemberRole Role, int OpenTasks, int DoneTasks);

    public sealed record ProjectsProps(IReadOnlyList<ProjectCard> Projects);

    // Properties rather than a positional record: ASP.NET's form binding cannot build a record whose optional
    // constructor parameter (Description) is missing from the post.
    public sealed record CreateProjectInput
    {
        [Required, StringLength(80, MinimumLength = 2)]
        public string Name { get; init; } = "";

        [Required, RegularExpression("^[a-z][a-z0-9-]{1,23}$", ErrorMessage = "Lowercase letters, digits and dashes, 2 to 24, starting with a letter.")]
        public string Slug { get; init; } = "";

        [StringLength(500)]
        public string? Description { get; init; }
    }

    public sealed record CreatedProject(string Slug);

    public static void MapProjects(this IEndpointRouteBuilder app)
    {
        app.MapPageLoader("/projects", async (ClaimsPrincipal user, TaskBoardDb db, CancellationToken cancellationToken) =>
        {
            var userId = user.UserId();
            var projects = await db.Memberships
                .Where(m => m.UserId == userId)
                .OrderBy(m => m.Project.Name)
                .Select(m => new ProjectCard(
                    m.Project.Slug,
                    m.Project.Name,
                    m.Project.Description,
                    m.Role,
                    m.Project.Tasks.Count(t => t.State != TaskState.Done),
                    m.Project.Tasks.Count(t => t.State == TaskState.Done)))
                .ToListAsync(cancellationToken);
            return Page.Props(new ProjectsProps(projects)).WithMetadata(new PageMetadata { Title = "Projects" });
        });

        app.MapAction("projects.create", async (
                [FromForm] CreateProjectInput input, ClaimsPrincipal user, TaskBoardDb db, TimeProvider time, CancellationToken cancellationToken) =>
            {
                var slug = input.Slug.ToLowerInvariant();
                if (await db.Projects.AnyAsync(p => p.Slug == slug, cancellationToken))
                {
                    return ServerAction.Invalid("slug", "That key is taken. Try another.");
                }

                var userId = user.UserId();
                var now = time.GetUtcNow();
                var project = new Project { Slug = slug, Name = input.Name.Trim(), Description = input.Description?.Trim() ?? "", CreatedAt = now };
                project.Members.Add(new Membership { UserId = userId, Role = MemberRole.Owner, JoinedAt = now });
                db.Projects.Add(project);
                db.LogActivity(project.Id, userId, null, $"created the project {project.Name}", now);
                await db.SaveChangesAsync(cancellationToken);
                // The list and the sidebar show it after the client's refresh.
                return ServerAction.Ok(new CreatedProject(slug)).Revalidate();
            })
            .RequireAuthorization();
    }
}
