namespace HuebRun.Api.Contracts;

public sealed record LookupItem(int Id, string NameTh, string? NameEn);

public sealed record LookupsResponse(
    IReadOnlyList<LookupItem> Titles,
    IReadOnlyList<LookupItem> Nationalities,
    IReadOnlyList<LookupItem> BloodTypes,
    IReadOnlyList<LookupItem> EmergencyRelations);
