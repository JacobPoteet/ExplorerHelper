namespace ExplorerHelper.Models;

/// <summary>
/// In-memory triage decision for a file. Nothing touches the disk until the user
/// commits — flags live only for the session and are cleared by a commit.
/// </summary>
public enum TriageFlag
{
    None,
    Keep,
    Reject,
}
