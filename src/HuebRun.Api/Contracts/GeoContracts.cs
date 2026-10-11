namespace HuebRun.Api.Contracts;

public sealed record GeoItem(int Id, string NameTh, string? NameEn);

public sealed record SubdistrictItem(int Id, string NameTh, string? NameEn, string ZipCode);

public sealed record GeoRef(int Id, string NameTh);

public sealed record ZipMatch(GeoRef Subdistrict, GeoRef District, GeoRef Province, string ZipCode);
