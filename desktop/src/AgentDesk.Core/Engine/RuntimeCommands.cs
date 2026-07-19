namespace AgentDesk.Core.Engine;

public sealed record RuntimeCommand(
    string Name,
    string Description,
    RuntimeCommandInput? Input = null,
    RuntimeSkillMetadata? Skill = null);

public sealed record RuntimeCommandInput(string Hint);

public sealed record RuntimeSkillMetadata(
    RuntimeSkillScope Scope,
    string Path);

public enum RuntimeSkillScope
{
    Local,
    Repo,
    User,
    Plugin,
}
