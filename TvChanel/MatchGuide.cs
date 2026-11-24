using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TvChanel;

public sealed record ListedMatch
{
    public required long Id { get; init; }
    public required string Home { get; init; }
    public required string Away { get; init; }
    public required string League { get; init; }
    public required DateTime KickoffUtc { get; init; }
    public required string Station { get; init; }
    public required string Score { get; init; }
    public required bool Started { get; init; }
    public required bool Finished { get; init; }

    public string Title => $"{Home} — {Away}";
}

public sealed class MatchBriefing
{
    public required string Text { get; init; }
}

public sealed class MatchGuide
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly Regex Noise = new(
        @"\b(hd|fhd|sd|uhd|4k|1080p|720p|hevc|raw|online)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "tv", "channel", "sport", "sports", "spor", "live", "network", "digital", "plus", "play", "the"
    };

    private static readonly (string Slug, string Hint)[] Leagues =
    [
        ("uefa.champions", "champion"),
        ("uefa.europa", "europa"),
        ("uefa.europa.conf", "conference"),
        ("eng.1", "premier"),
        ("esp.1", "laliga"),
        ("esp.1", "la liga"),
        ("ger.1", "bundesliga"),
        ("ita.1", "serie a"),
        ("fra.1", "ligue 1"),
        ("tur.1", "super lig"),
        ("ned.1", "eredivisie"),
        ("por.1", "primeira"),
        ("bel.1", "pro league"),
        ("usa.1", "mls"),
        ("mex.1", "liga mx"),
        ("sco.1", "premiership")
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTime At, List<ListedMatch> Rows)> _tvCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JsonElement> _scoreboards = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _boardLock = new();

    static MatchGuide()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        Http.DefaultRequestHeaders.Referrer = new Uri("https://www.fotmob.com/");
    }

    public async Task<IReadOnlyList<ListedMatch>> ForChannelAsync(
        PlayableChannel channel,
        CancellationToken cancellationToken)
    {
        var country = NormalizeCountry(channel.CountryCode);
        var rows = await TelevisionAsync(country, cancellationToken);
        return rows
            .Where(match => StationFits(channel.Name, match.Station))
            .OrderBy(match => match.KickoffUtc)
            .ToList();
    }

    public async Task<MatchBriefing> BriefingAsync(ListedMatch match, CancellationToken cancellationToken)
    {
        using var detail = await GetJsonAsync(
            $"https://www.fotmob.com/api/data/matchDetails?matchId={match.Id}",
            cancellationToken);
        var root = detail.RootElement;
        var facts = root.GetProperty("content").GetProperty("matchFacts");
        var info = facts.GetProperty("infoBox");

        var text = new StringBuilder();
        text.AppendLine(match.Title);
        text.AppendLine(match.League);
        text.AppendLine(match.KickoffUtc.ToLocalTime().ToString("dd MMM, HH:mm", CultureInfo.CurrentCulture));
        text.AppendLine("Yayım: " + match.Station);
        if (!string.IsNullOrWhiteSpace(match.Score) && match.Score != "0 - 0")
            text.AppendLine("Hesab: " + match.Score);

        var stadium = ReadStadium(info);
        var referee = ReadText(info, "Referee", "text");
        if (stadium.Length > 0)
            text.AppendLine("Stadion: " + stadium);
        if (referee.Length > 0)
            text.AppendLine("Hakim: " + referee);

        text.AppendLine();
        text.AppendLine("Bukmeker xətti");
        var odds = await FindOddsAsync(match, cancellationToken);
        text.AppendLine(odds ?? "Bu matç üçün bukmeker xətti tapılmadı.");

        text.AppendLine();
        text.AppendLine("Forma (son oyunlar)");
        AppendForm(text, facts, match.Home, match.Away);

        text.AppendLine();
        text.AppendLine("Qarşılıqlı nəticələr");
        AppendH2h(text, root);

        AppendInsights(text, facts);
        AppendStats(text, root);
        return new MatchBriefing { Text = text.ToString().TrimEnd() };
    }

    private async Task<List<ListedMatch>> TelevisionAsync(string country, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_tvCache.TryGetValue(country, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromHours(2))
                return cached.Rows;

            var schedule = await UpcomingAsync(cancellationToken);
            var found = new ConcurrentBag<ListedMatch>();
            await Parallel.ForEachAsync(
                schedule,
                new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = cancellationToken },
                async (match, token) =>
                {
                    try
                    {
                        using var listing = await GetJsonAsync(
                            $"https://www.fotmob.com/api/data/tvlisting?matchId={match.Id}&countryCode={country}",
                            token);
                        foreach (var station in ReadStations(listing.RootElement))
                        {
                            found.Add(match with { Station = station });
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (HttpRequestException)
                    {
                        // One listing can fail without dropping the rest.
                    }
                    catch (JsonException)
                    {
                    }
                });

            var rows = found.ToList();
            _tvCache[country] = (DateTime.UtcNow, rows);
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<List<ListedMatch>> UpcomingAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var rows = new List<ListedMatch>();
        for (var day = 0; day <= 2; day++)
        {
            var date = now.Date.AddDays(day).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            using var doc = await GetJsonAsync(
                $"https://www.fotmob.com/api/data/matches?date={date}&timezone=UTC",
                cancellationToken);

            if (!doc.RootElement.TryGetProperty("leagues", out var leagues))
                continue;

            foreach (var league in leagues.EnumerateArray())
            {
                var leagueName = ReadString(league, "name");
                if (!league.TryGetProperty("matches", out var matches))
                    continue;

                foreach (var match in matches.EnumerateArray())
                {
                    if (!match.TryGetProperty("status", out var status))
                        continue;
                    if (ReadBool(status, "cancelled"))
                        continue;

                    if (!status.TryGetProperty("utcTime", out var utcElement))
                        continue;
                    if (!utcElement.TryGetDateTime(out var kickoff))
                        continue;
                    kickoff = kickoff.ToUniversalTime();
                    if (kickoff < now.AddHours(-3) || kickoff > now.AddHours(48))
                        continue;

                    var finished = ReadBool(status, "finished");
                    if (finished && kickoff < now.AddHours(-3))
                        continue;

                    rows.Add(new ListedMatch
                    {
                        Id = match.GetProperty("id").GetInt64(),
                        Home = ReadString(match.GetProperty("home"), "name"),
                        Away = ReadString(match.GetProperty("away"), "name"),
                        League = leagueName,
                        KickoffUtc = kickoff,
                        Station = "",
                        Score = ReadString(status, "scoreStr"),
                        Started = ReadBool(status, "started"),
                        Finished = finished
                    });
                }
            }
        }

        return rows
            .OrderBy(match => LeagueRank(match.League))
            .ThenBy(match => match.KickoffUtc)
            .Take(60)
            .ToList();
    }

    private async Task<string?> FindOddsAsync(ListedMatch match, CancellationToken cancellationToken)
    {
        var date = match.KickoffUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var league = match.League.ToLowerInvariant();
        var slugs = Leagues
            .OrderByDescending(item => league.Contains(item.Hint, StringComparison.Ordinal))
            .Select(item => item.Slug)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var slug in slugs)
        {
            JsonElement board;
            var key = slug + "|" + date;
            var cached = false;
            lock (_boardLock)
                cached = _scoreboards.TryGetValue(key, out board);

            if (!cached)
            {
                try
                {
                    using var doc = await GetJsonAsync(
                        $"https://site.api.espn.com/apis/site/v2/sports/soccer/{slug}/scoreboard?dates={date}",
                        cancellationToken);
                    board = doc.RootElement.Clone();
                    lock (_boardLock)
                        _scoreboards[key] = board;
                }
                catch (HttpRequestException)
                {
                    continue;
                }
                catch (JsonException)
                {
                    continue;
                }
            }

            if (!board.TryGetProperty("events", out var events))
                continue;

            foreach (var evt in events.EnumerateArray())
            {
                if (!evt.TryGetProperty("competitions", out var competitions))
                    continue;
                foreach (var competition in competitions.EnumerateArray())
                {
                    if (!TryReadSides(competition, out var home, out var away))
                        continue;
                    if (!SameTeams(match.Home, match.Away, home, away))
                        continue;
                    if (!competition.TryGetProperty("odds", out var odds))
                        return null;
                    return FormatOdds(odds, home, away);
                }
            }
        }

        return null;
    }

    private static string? FormatOdds(JsonElement odds, string home, string away)
    {
        if (odds.ValueKind == JsonValueKind.Array)
            odds = odds.GetArrayLength() == 0 ? default : odds[0];
        if (odds.ValueKind != JsonValueKind.Object)
            return null;

        var book = "Bukmeker";
        if (odds.TryGetProperty("provider", out var provider))
            book = ReadString(provider, "displayName");
        if (book.Length == 0)
            book = "Bukmeker";

        if (!odds.TryGetProperty("moneyline", out var moneyline))
            return null;

        var lines = new StringBuilder();
        lines.AppendLine("Mənbə: " + book);
        AppendPrice(lines, "1  " + home, ReadClose(moneyline, "home"));
        AppendPrice(lines, "X  Heç-heçə", ReadClose(moneyline, "draw"));
        AppendPrice(lines, "2  " + away, ReadClose(moneyline, "away"));

        if (odds.TryGetProperty("total", out var total))
        {
            var over = ReadTotal(total, "over");
            var under = ReadTotal(total, "under");
            if (over.Line.Length > 0)
                AppendPrice(lines, "Üst " + over.Line.TrimStart('o', 'O'), over.Price);
            if (under.Line.Length > 0)
                AppendPrice(lines, "Alt " + under.Line.TrimStart('u', 'U'), under.Price);
        }

        var text = lines.ToString().TrimEnd();
        return text.Length == 0 ? null : text;
    }

    private static void AppendPrice(StringBuilder lines, string label, string american)
    {
        if (!int.TryParse(american, NumberStyles.Integer, CultureInfo.InvariantCulture, out var price))
            return;
        var decimalOdds = price > 0
            ? 1 + price / 100d
            : 1 + 100d / Math.Abs(price);
        lines.AppendLine($"{label,-28} {decimalOdds,5:0.00}   ({american})");
    }

    private static string ReadClose(JsonElement moneyline, string side)
    {
        if (!moneyline.TryGetProperty(side, out var node))
            return "";
        if (node.TryGetProperty("close", out var close))
            return ReadString(close, "odds");
        return ReadString(node, "odds");
    }

    private static (string Line, string Price) ReadTotal(JsonElement total, string side)
    {
        if (!total.TryGetProperty(side, out var node) || !node.TryGetProperty("close", out var close))
            return ("", "");
        return (ReadString(close, "line"), ReadString(close, "odds"));
    }

    private static bool TryReadSides(JsonElement competition, out string home, out string away)
    {
        home = "";
        away = "";
        if (!competition.TryGetProperty("competitors", out var competitors))
            return false;
        foreach (var competitor in competitors.EnumerateArray())
        {
            var side = ReadString(competitor, "homeAway");
            var name = competitor.TryGetProperty("team", out var team) ? ReadString(team, "displayName") : "";
            if (side.Equals("home", StringComparison.OrdinalIgnoreCase))
                home = name;
            else if (side.Equals("away", StringComparison.OrdinalIgnoreCase))
                away = name;
        }

        return home.Length > 0 && away.Length > 0;
    }

    private static void AppendForm(StringBuilder text, JsonElement facts, string home, string away)
    {
        if (!facts.TryGetProperty("teamForm", out var form) || form.ValueKind != JsonValueKind.Array)
        {
            text.AppendLine("Forma yoxdur.");
            return;
        }

        var sides = form.EnumerateArray().Take(2).ToList();
        if (sides.Count > 0)
            text.AppendLine(home + ": " + FormLetters(sides[0]));
        if (sides.Count > 1)
            text.AppendLine(away + ": " + FormLetters(sides[1]));
    }

    private static string FormLetters(JsonElement side)
    {
        var items = side.ValueKind == JsonValueKind.Object && side.TryGetProperty("value", out var value)
            ? value
            : side;
        if (items.ValueKind != JsonValueKind.Array)
            return "—";

        var letters = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            var letter = ReadString(item, "resultString");
            if (letter.Length > 0)
                letters.Add(letter);
        }

        return letters.Count == 0 ? "—" : string.Join(" ", letters);
    }

    private static void AppendH2h(StringBuilder text, JsonElement root)
    {
        if (!root.GetProperty("content").TryGetProperty("h2h", out var h2h) || h2h.ValueKind != JsonValueKind.Object)
        {
            text.AppendLine("Qarşılıqlı tarixçə yoxdur.");
            return;
        }

        if (h2h.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Array)
        {
            var numbers = summary.EnumerateArray().Select(item => item.ToString()).Take(3).ToArray();
            if (numbers.Length == 3)
                text.AppendLine($"Ev {numbers[0]}   Heç-heçə {numbers[1]}   Qonaq {numbers[2]}");
        }

        if (!h2h.TryGetProperty("matches", out var matches))
            return;

        var shown = 0;
        foreach (var item in matches.EnumerateArray())
        {
            if (shown == 5)
                break;
            var home = item.TryGetProperty("home", out var homeTeam) ? ReadString(homeTeam, "name") : "";
            var away = item.TryGetProperty("away", out var awayTeam) ? ReadString(awayTeam, "name") : "";
            var score = item.TryGetProperty("status", out var status) ? ReadString(status, "scoreStr") : "";
            if (score.Length == 0 && item.TryGetProperty("status", out status))
            {
                var homeScore = ReadString(status, "homeScore");
                var awayScore = ReadString(status, "awayScore");
                if (homeScore.Length > 0 && awayScore.Length > 0)
                    score = homeScore + " - " + awayScore;
            }

            text.AppendLine(score.Length == 0 ? $"{home} — {away}" : $"{home} {score} {away}");
            shown++;
        }
    }

    private static void AppendInsights(StringBuilder text, JsonElement facts)
    {
        if (!facts.TryGetProperty("insights", out var insights) || insights.ValueKind != JsonValueKind.Array)
            return;

        var lines = insights.EnumerateArray()
            .Select(item => ReadString(item, "text"))
            .Where(line => line.Length > 0)
            .Take(4)
            .ToList();
        if (lines.Count == 0)
            return;

        text.AppendLine();
        text.AppendLine("Statistik qeydlər");
        foreach (var line in lines)
            text.AppendLine("• " + line);
    }

    private static void AppendStats(StringBuilder text, JsonElement root)
    {
        var content = root.GetProperty("content");
        if (!content.TryGetProperty("stats", out var stats) || stats.ValueKind != JsonValueKind.Object)
            return;
        if (!stats.TryGetProperty("Periods", out var periods) || !periods.TryGetProperty("All", out var all))
            return;
        if (!all.TryGetProperty("stats", out var groups))
            return;

        var wroteHeader = false;
        foreach (var group in groups.EnumerateArray())
        {
            var key = ReadString(group, "key");
            if (key is not ("top_stats" or "shots" or "expected_goals"))
                continue;
            if (!group.TryGetProperty("stats", out var rows))
                continue;

            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("stats", out var pair) || pair.GetArrayLength() < 2)
                    continue;
                if (!wroteHeader)
                {
                    text.AppendLine();
                    text.AppendLine("Oyun statistikası");
                    wroteHeader = true;
                }

                text.AppendLine($"{FormatStat(pair[0]),6}  {ReadString(row, "title"),-24} {FormatStat(pair[1])}");
            }
        }
    }

    private static string FormatStat(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.ToString(),
        JsonValueKind.String => value.GetString() ?? "",
        _ => ""
    };

    private static IEnumerable<string> ReadStations(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("channels", out var channels) && channels.ValueKind == JsonValueKind.Array)
        {
            foreach (var channel in channels.EnumerateArray())
            {
                var name = ReadString(channel, "name");
                if (name.Length > 0)
                    names.Add(name);
            }
        }

        if (names.Count == 0)
        {
            var single = ReadString(root, "name");
            if (single.Length > 0)
                names.Add(single);
        }

        return names;
    }

    private static int LeagueRank(string league)
    {
        var name = league.ToLowerInvariant();
        string[] featured =
        [
            "champions", "europa", "conference", "premier", "laliga", "la liga", "bundesliga",
            "serie a", "ligue 1", "super lig", "süper", "eredivisie", "primeira", "mls",
            "championship", "world cup", "fa cup", "copa"
        ];
        return featured.Any(name.Contains) ? 0 : 1;
    }

    private static bool StationFits(string channelName, string station)
    {
        var channel = NormalizeName(channelName);
        var broadcaster = NormalizeName(station);
        if (channel.Length == 0 || broadcaster.Length == 0)
            return false;

        var channelNumber = TrailingNumber(channel);
        var stationNumber = TrailingNumber(broadcaster);
        if (channelNumber != null && stationNumber != null && channelNumber != stationNumber)
            return false;

        if (channel == broadcaster)
            return true;

        var shorter = channel.Length <= broadcaster.Length ? channel : broadcaster;
        var longer = channel.Length <= broadcaster.Length ? broadcaster : channel;
        if (shorter.Length >= 8 && longer.Contains(shorter, StringComparison.Ordinal))
            return true;

        var left = Tokens(channel);
        var right = Tokens(broadcaster);
        var shared = left.Intersect(right, StringComparer.Ordinal).Count();
        if (shared >= 2)
            return true;
        return shared == 1 && left.Intersect(right, StringComparer.Ordinal).Any(token => token.Length >= 8);
    }

    private static HashSet<string> Tokens(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3 && !Stopwords.Contains(token) && !int.TryParse(token, out _))
            .ToHashSet(StringComparer.Ordinal);

    private static string? TrailingNumber(string normalized)
    {
        var match = Regex.Match(normalized, @"(\d+)$");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string NormalizeName(string value)
    {
        var text = Noise.Replace(value, " ").ToLowerInvariant();
        text = Regex.Replace(text, @"[^\p{L}\p{N}]+", " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string NormalizeCountry(string code)
    {
        var country = string.IsNullOrWhiteSpace(code) ? "AZ" : code.Trim().ToUpperInvariant();
        return country == "UK" ? "GB" : country;
    }

    private static bool SameTeams(string homeA, string awayA, string homeB, string awayB)
    {
        var a1 = Key(homeA);
        var a2 = Key(awayA);
        var b1 = Key(homeB);
        var b2 = Key(awayB);
        return Pair(a1, b1) && Pair(a2, b2) || Pair(a1, b2) && Pair(a2, b1);
    }

    private static bool Pair(string left, string right) =>
        left.Length > 0 && right.Length > 0 && (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal));

    private static string Key(string name)
    {
        var form = name.Normalize(NormalizationForm.FormD);
        var chars = form.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(ch));
        return new string(chars.ToArray()).ToLowerInvariant();
    }

    private static string ReadStadium(JsonElement info)
    {
        if (!info.TryGetProperty("Stadium", out var stadium) || stadium.ValueKind != JsonValueKind.Object)
            return "";
        var name = ReadString(stadium, "name");
        var city = ReadString(stadium, "city");
        return city.Length == 0 ? name : $"{name}, {city}";
    }

    private static string ReadText(JsonElement info, string node, string field)
    {
        if (!info.TryGetProperty(node, out var child) || child.ValueKind != JsonValueKind.Object)
            return "";
        return ReadString(child, field);
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            _ => ""
        };
    }

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
