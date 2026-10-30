using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskBoard.Data;
using TaskBoard.Infrastructure;

namespace TaskBoard.Features.Members;

/// <summary>/projects/[id]/settings: who is on the project, and (for owners) adding people by email.</summary>
public static class MembersEndpoints
{
    public sealed record MemberRow(Guid Id, string Name, string Email, MemberRole Role, DateTimeOffset JoinedAt);

    public sealed record MembersProps(IReadOnlyList<MemberRow> Members, bool CanManage);

    public sealed record AddMemberInput
    {
        [Required] public string ProjectSlug { get; init; } = "";

        [Required, EmailAddress, StringLength(254)]
        public string Email { get; init; } = "";
    }

    public static void MapMembers(this IEndpointRouteBuilder app)
    {
        app.MapPageLoader("/projects/[id]/settings", async (string id, ClaimsPrincipal user, TaskBoardDb db, CancellationToken cancellationToken) =>
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

            var members = await db.Memberships
                .Where(m => m.ProjectId == access.ProjectId)
                .OrderBy(m => m.JoinedAt)
                .Select(m => new { m.UserId, m.User.Name, m.User.Email, m.Role, m.JoinedAt })
                .ToListAsync(cancellationToken);
            return Page.Props(new MembersProps(
                    [.. members.Select(m => new MemberRow(m.UserId, m.Name, m.Email, m.Role, m.JoinedAt))],
                    access.IsOwner))
                .WithMetadata(new PageMetadata { Title = $"Members · {access.Name}" });
        });

        app.MapAction("projects.addMember", async (
                [FromForm] AddMemberInput input, ClaimsPrincipal user, TaskBoardDb db, TimeProvider time, CancellationToken cancellationToken) =>
            {
                var userId = user.UserId();
                var access = await db.AccessAsync(input.ProjectSlug, userId, cancellationToken);
                if (access is not { IsOwner: true })
                {
                    return access is null ? ServerAction.NotFound() : ServerAction.Forbidden();
                }

                var email = input.Email.Trim().ToLowerInvariant();
                var person = await db.Users.Where(u => u.Email == email).Select(u => new { u.Id, u.Name }).SingleOrDefaultAsync(cancellationToken);
                if (person is null)
                {
                    return ServerAction.Invalid("email", "Nobody has an account with that email yet. Ask them to sign up first.");
                }

                if (await db.Memberships.AnyAsync(m => m.ProjectId == access.ProjectId && m.UserId == person.Id, cancellationToken))
                {
                    return ServerAction.Invalid("email", $"{person.Name} is already on this project.");
                }

                var now = time.GetUtcNow();
                db.Memberships.Add(new Membership { ProjectId = access.ProjectId, UserId = person.Id, Role = MemberRole.Member, JoinedAt = now });
                db.LogActivity(access.ProjectId, userId, null, $"added {person.Name} to the project", now);
                await db.SaveChangesAsync(cancellationToken);
                return ServerAction.Ok().RevalidatePath($"/projects/{access.Slug}/settings");
            })
            .RequireAuthorization();
    }
}
