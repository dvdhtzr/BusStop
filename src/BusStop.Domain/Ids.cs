namespace BusStop.Domain;

/// <summary>Strongly typed identifiers. Using distinct struct types prevents an id of one
/// kind being passed where another is expected, which matters for context isolation and
/// correct attribution.</summary>
public readonly record struct CharacterId(Guid Value)
{
    public static CharacterId New() => new(Guid.NewGuid());
    public static CharacterId Parse(string s) => new(Guid.Parse(s));
    public override string ToString() => Value.ToString();
}

public readonly record struct SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.NewGuid());
    public static SessionId Parse(string s) => new(Guid.Parse(s));
    public override string ToString() => Value.ToString();
}

public readonly record struct BriefingId(Guid Value)
{
    public static BriefingId New() => new(Guid.NewGuid());
    public static BriefingId Parse(string s) => new(Guid.Parse(s));
    public override string ToString() => Value.ToString();
}

public readonly record struct OpportunityId(Guid Value)
{
    public static OpportunityId New() => new(Guid.NewGuid());
    public static OpportunityId Parse(string s) => new(Guid.Parse(s));
    public override string ToString() => Value.ToString();
}

public readonly record struct UtteranceId(Guid Value)
{
    public static UtteranceId New() => new(Guid.NewGuid());
    public static UtteranceId Parse(string s) => new(Guid.Parse(s));
    public override string ToString() => Value.ToString();
}

public readonly record struct AttemptId(Guid Value)
{
    public static AttemptId New() => new(Guid.NewGuid());
    public static AttemptId Parse(string s) => new(Guid.Parse(s));
    public override string ToString() => Value.ToString();
}
