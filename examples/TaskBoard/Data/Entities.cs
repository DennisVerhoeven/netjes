namespace TaskBoard.Data;

public sealed class User
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Lower-cased: the unique index makes sign-up case-insensitive.</summary>
    public required string Email { get; init; }

    public required string Name { get; set; }
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; init; }
}

public enum MemberRole { Owner, Member }

public sealed class Project
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>The URL segment, e.g. <c>web</c>; tasks are numbered <c>WEB-1</c>, <c>WEB-2</c>, ...</summary>
    public required string Slug { get; init; }

    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public int NextTaskNumber { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; init; }

    public List<Membership> Members { get; } = [];
    public List<TaskItem> Tasks { get; } = [];
}

public sealed class Membership
{
    public Guid ProjectId { get; init; }
    public Guid UserId { get; init; }
    public MemberRole Role { get; set; }
    public DateTimeOffset JoinedAt { get; init; }

    public Project Project { get; init; } = null!;
    public User User { get; init; } = null!;
}

public enum TaskState { Backlog, Todo, InProgress, Done }

public enum TaskPriority { None, Low, Medium, High, Urgent }

public sealed class TaskItem
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid ProjectId { get; init; }

    /// <summary>Per project: the <c>12</c> in <c>WEB-12</c> and in <c>/projects/web/tasks/12</c>.</summary>
    public int Number { get; init; }

    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public TaskState State { get; set; }
    public TaskPriority Priority { get; set; }
    public Guid? AssigneeId { get; set; }
    public Guid CreatedById { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Project Project { get; init; } = null!;
    public User? Assignee { get; set; }
    public List<Comment> Comments { get; } = [];
}

public sealed class Comment
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TaskId { get; init; }
    public Guid AuthorId { get; init; }
    public required string Body { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public TaskItem Task { get; init; } = null!;
    public User Author { get; init; } = null!;
}

/// <summary>What happened in a project, newest first on the task page's activity feed.</summary>
public sealed class ActivityEntry
{
    public long Id { get; init; }
    public Guid ProjectId { get; init; }
    public int? TaskNumber { get; init; }
    public Guid ActorId { get; init; }
    public required string Summary { get; init; }
    public DateTimeOffset At { get; init; }

    public User Actor { get; init; } = null!;
}
