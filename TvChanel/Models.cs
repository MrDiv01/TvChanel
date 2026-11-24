namespace TvChanel;

public sealed class ChannelDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Country { get; set; } = "";
    public List<string> Categories { get; set; } = [];
    public bool IsNsfw { get; set; }
    public string? Closed { get; set; }
}

public sealed class StreamDto
{
    public string? Channel { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public string? Quality { get; set; }
    public List<string> Labels { get; set; } = [];
}

public sealed class CategoryDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class CountryDto
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Flag { get; set; }
}

public sealed class PlayableChannel
{
    public required string Name { get; init; }
    public required string CountryCode { get; init; }
    public required string CountryName { get; init; }
    public required string[] Categories { get; init; }
    public required string Url { get; init; }
    public string? Quality { get; init; }
    public string? Referrer { get; init; }
    public string? UserAgent { get; init; }
    public string Labels { get; init; } = "";

    public bool IsGeoBlocked =>
        Labels.Contains("Geo-blocked", StringComparison.OrdinalIgnoreCase);
}

public sealed class CatalogSnapshot
{
    public required IReadOnlyList<PlayableChannel> Channels { get; init; }
    public required IReadOnlyList<CategoryDto> Categories { get; init; }
    public required IReadOnlyList<CountryDto> Countries { get; init; }
}
