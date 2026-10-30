using Microsoft.EntityFrameworkCore;
using TaskBoard.Data;

namespace TaskBoard.Infrastructure;

/// <summary>
/// Who may see a project: members only. Every slice that takes a project slug from the URL checks it here, so the
/// tenancy rule lives in one place.
/// </summary>
public sealed record ProjectAccess(Guid ProjectId, string Slug, string Name, MemberRole? Role)
{
    public bool IsMember => Role is not null;
    public bool IsOwner => Role == MemberRole.Owner;
}

public static class ProjectAccessQueries
{
    /// <summary>The project behind <paramref name="slug"/> and the user's role in it; null if there is no such project.</summary>
    public static Task<ProjectAccess?> AccessAsync(this TaskBoardDb db, string slug, Guid userId, CancellationToken cancellationToken) =>
        db.Projects
            .Where(p => p.Slug == slug.ToLowerInvariant())
            .Select(p => new ProjectAccess(
                p.Id,
                p.Slug,
                p.Name,
                p.Members.Where(m => m.UserId == userId).Select(m => (MemberRole?)m.Role).FirstOrDefault()))
            .FirstOrDefaultAsync(cancellationToken);

    public static void LogActivity(this TaskBoardDb db, Guid projectId, Guid actorId, int? taskNumber, string summary, DateTimeOffset at) =>
        db.Activity.Add(new ActivityEntry { ProjectId = projectId, ActorId = actorId, TaskNumber = taskNumber, Summary = summary, At = at });
}
