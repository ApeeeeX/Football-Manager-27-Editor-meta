namespace Fm27Editor.Core.Offsets;

/// <summary>Profile locations in a match player's temporary copy and its link to the permanent person.</summary>
public sealed record InMatchOffsets(uint PlayerVtable, uint DataVtable, uint PersonVtable, uint PlayerStaffPersonVtable,
    int Data, int SourcePerson, int PersonUid, int Fixture, int Side, int PositionBase, int AttributeBase,
    int InitialCondition, int Condition, int? Morale, int? LiveCondition = null);
