namespace BusStop.Domain;

/// <summary>The public face of a participant in a room: the id and the display name others address
/// them by. It carries no private identity, which is why it is safe to show to other participants.</summary>
public sealed record ParticipantHandle(CharacterId Id, string Name);
