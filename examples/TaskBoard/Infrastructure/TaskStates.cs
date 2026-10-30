using TaskBoard.Data;

namespace TaskBoard.Infrastructure;

/// <summary>
/// Board order, labels, and parsing states and priorities from URLs and forms. Props carry the enums themselves, which
/// the JSON options write as camelCase strings.
/// </summary>
public static class TaskStates
{
    public static readonly TaskState[] All = [TaskState.Backlog, TaskState.Todo, TaskState.InProgress, TaskState.Done];

    public static string Label(TaskState state) => state switch
    {
        TaskState.Backlog => "Backlog",
        TaskState.Todo => "Todo",
        TaskState.InProgress => "In progress",
        _ => "Done",
    };

    public static bool TryParse(string? value, out TaskState state) =>
        Enum.TryParse(value, ignoreCase: true, out state) && Enum.IsDefined(state);

    public static string Name(TaskPriority priority) => priority.ToString().ToLowerInvariant();

    public static bool TryParse(string? value, out TaskPriority priority) =>
        Enum.TryParse(value, ignoreCase: true, out priority) && Enum.IsDefined(priority);
}
