namespace IssueAgent.Domain;

/// <summary>Transient human workflow commands represented by labels.</summary>
public enum WorkflowCommand
{
    Replan,
    Implement,
    Revise,
    Continue,
    Cancel,
}

public static class WorkflowCommandLabels
{
    public const string Replan = "agent:cmd:replan";
    public const string Implement = "agent:cmd:implement";
    public const string Revise = "agent:cmd:revise";
    public const string Continue = "agent:cmd:continue";
    public const string Cancel = "agent:cmd:cancel";

    public static bool TryParse(string label, out WorkflowCommand command)
    {
        command = label switch
        {
            Replan => WorkflowCommand.Replan,
            Implement => WorkflowCommand.Implement,
            Revise => WorkflowCommand.Revise,
            Continue => WorkflowCommand.Continue,
            Cancel => WorkflowCommand.Cancel,
            _ => default,
        };

        return label is Replan or Implement or Revise or Continue or Cancel;
    }
}
