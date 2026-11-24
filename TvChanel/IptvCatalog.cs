using System.Net;
using System.Text.Json;

namespace TvChanel;

public sealed class IptvCatalog
{
    private const string BaseUrl = "https://iptv-org.github.io/api/";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(90)
    };

    private readonly string _cacheDir;

    static IptvCatalog()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("TvChanel/1.0");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public IptvCatalog()
    {
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TvChanel");
    }

    public async Task<CatalogSnapshot> LoadAsync(bool force, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDir);

        var files = new (string Name, string Url)[]
        {
            ("channels.json", BaseUrl + "channels.json"),
            ("streams.json", BaseUrl + "streams.json"),
            ("categories.json", BaseUrl + "categories.json"),
            ("countries.json", BaseUrl + "countries.json")
        };

        var stampPath = Path.Combine(_cacheDir, "stamp.txt");
        var fresh = !force
            && File.Exists(stampPath)
            && DateTime.UtcNow - File.GetLastWriteTimeUtc(stampPath) < CacheTtl
            && files.All(f => File.Exists(Path.Combine(_cacheDir, f.Name)));

        if (!fresh)
        {
            await Task.WhenAll(files.Select(f => DownloadAsync(f.Name, f.Url, cancellationToken)));
            await File.WriteAllTextAsync(stampPath, DateTime.UtcNow.ToString("O"), cancellationToken);
        }

        var channels = await ReadAsync<List<ChannelDto>>("channels.json", cancellationToken) ?? [];
        var streams = await ReadAsync<List<StreamDto>>("streams.json", cancellationToken) ?? [];
        var categories = await ReadAsync<List<CategoryDto>>("categories.json", cancellationToken) ?? [];
        var countries = await ReadAsync<List<CountryDto>>("countries.json", cancellationToken) ?? [];

        return Build(channels, streams, categories, countries);
    }

    private async Task DownloadAsync(string name, string url, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_cacheDir, name);
        var temp = path + ".tmp";

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(temp))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        File.Move(temp, path, overwrite: true);
    }

    private async Task<T?> ReadAsync<T>(string name, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path.Combine(_cacheDir, name));
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private static CatalogSnapshot Build(
        List<ChannelDto> channels,
        List<StreamDto> streams,
        List<CategoryDto> categories,
        List<CountryDto> countries)
    {
        var channelById = new Dictionary<string, ChannelDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in channels)
        {
            if (string.IsNullOrWhiteSpace(channel.Id) || !string.IsNullOrWhiteSpace(channel.Closed))
                continue;
            if (channel.IsNsfw || channel.Categories.Any(c => c.Equals("xxx", StringComparison.OrdinalIgnoreCase)))
                continue;
            channelById.TryAdd(channel.Id, channel);
        }

        var countryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var country in countries)
        {
            if (string.IsNullOrWhiteSpace(country.Code))
                continue;
            countryNames.TryAdd(country.Code, string.IsNullOrWhiteSpace(country.Name) ? country.Code : country.Name);
        }

        var playable = new List<PlayableChannel>(streams.Count);
        var usedCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var stream in streams)
        {
            if (string.IsNullOrWhiteSpace(stream.Url) || string.IsNullOrWhiteSpace(stream.Channel))
                continue;
            if (!channelById.TryGetValue(stream.Channel, out var channel))
                continue;

            var code = channel.Country ?? "";
            if (code.Length > 0)
                usedCountries.Add(code);

            var countryName = code.Length == 0
                ? "—"
                : countryNames.GetValueOrDefault(code, code);

            playable.Add(new PlayableChannel
            {
                Name = string.IsNullOrWhiteSpace(stream.Title) ? channel.Name : stream.Title,
                CountryCode = code,
                CountryName = countryName,
                Categories = channel.Categories.Count == 0 ? ["general"] : channel.Categories.ToArray(),
                Url = stream.Url,
                Quality = stream.Quality,
                Referrer = stream.Referrer,
                UserAgent = stream.UserAgent,
                Labels = string.Join(", ", stream.Labels)
            });
        }

        playable.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

        var visibleCategories = categories
            .Where(c => !c.Id.Equals("xxx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Id.Equals("sports", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var visibleCountries = countries
            .Where(c => usedCountries.Contains(c.Code))
            .OrderBy(c => c.Code.Equals("AZ", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new CatalogSnapshot
        {
            Channels = playable,
            Categories = visibleCategories,
            Countries = visibleCountries
        };
    }
}
