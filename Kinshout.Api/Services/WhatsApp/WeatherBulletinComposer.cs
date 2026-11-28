namespace Kinshout.Api.Services.WhatsApp;

public sealed record WeatherHour(
    DateTime Local,
    double Temperature,
    double Apparent,
    int RainProbability,
    double PrecipitationMm,
    int Code,
    double WindKmh);

public sealed record WeatherDay(
    DateTime LocalDate,
    double Minimum,
    double Maximum,
    double ApparentMinimum,
    double ApparentMaximum,
    IReadOnlyList<WeatherHour> Hours);

public sealed class StoredForecast
{
    public DateTime LocalDate { get; set; }
    public double Minimum { get; set; }
    public double Maximum { get; set; }
    public double ApparentMinimum { get; set; }
    public double ApparentMaximum { get; set; }
    public List<WeatherHour> Hours { get; set; } = [];
}

public sealed record WeatherBulletin(string Body, string TraceJson, bool SignificantChange);

/// <summary>Kinshasa forecast text. Figures come only from the supplied hours.</summary>
public static class WeatherBulletinComposer
{
    public static WeatherBulletin Compose(WeatherDay day, bool attribute, WeatherDay? previous = null, int rainHighlightMinimum = 20)
    {
        var morning = Slice(day.Hours, 6, 12);
        var afternoon = Slice(day.Hours, 12, 18);
        var evening = Slice(day.Hours, 18, 24);
        var periods = new[] { morning, afternoon, evening };
        var peak = periods.Max(p => p.MaxRain);
        var advice = Advice(day, periods);
        var lines = new List<string>
        {
            "🌤️ KIN MÉTÉO | by Kinoiserie",
            "",
            $"📅 {FrenchDate(day.LocalDate)}",
            "Prévision pour Kinshasa",
            "",
            "Mbote Kinshasa ! 🇨🇩",
            "",
            $"🌡️ *Températures : {Whole(day.Minimum)}°C – {Whole(day.Maximum)}°C*",
            "",
        };
        for (var i = 0; i < periods.Length; i++)
        {
            if (i > 0)
                lines.Add("");
            lines.AddRange(PeriodLines(periods[i], peak, rainHighlightMinimum));
        }
        lines.Add("");
        lines.Add("💡 *Le conseil du jour*");
        lines.Add(advice);
        lines.Add("");
        lines.Add("Bonne journée, les Kinois ! ❤️");
        if (attribute)
        {
            lines.Add("");
            lines.Add("Météo : Open-Meteo");
        }
        lines.Add("");
        lines.Add("🌐 https://kinoiserie.com");
        var body = string.Join("\n", lines);
        var changed = previous is not null && IsSignificant(day, previous);
        var trace = System.Text.Json.JsonSerializer.Serialize(new
        {
            provider = "Open-Meteo",
            licence = "CC BY 4.0",
            attributionIncluded = attribute,
            day = day.LocalDate.ToString("yyyy-MM-dd"),
            minimum = day.Minimum,
            maximum = day.Maximum,
            apparentMaximum = day.ApparentMaximum,
        });
        return new WeatherBulletin(body, trace, changed);
    }

    public static bool IsSignificant(WeatherDay current, WeatherDay previous)
    {
        if (Math.Abs(current.Maximum - previous.Maximum) >= 3)
            return true;
        if (Math.Abs(current.Minimum - previous.Minimum) >= 3)
            return true;
        var currentPeak = current.Hours.Select(h => h.RainProbability).DefaultIfEmpty(0).Max();
        var previousPeak = previous.Hours.Select(h => h.RainProbability).DefaultIfEmpty(0).Max();
        if (Math.Abs(currentPeak - previousPeak) >= 30)
            return true;
        var currentStorm = current.Hours.Any(h => h.Code >= 95);
        var previousStorm = previous.Hours.Any(h => h.Code >= 95);
        return currentStorm != previousStorm;
    }

    private static Period Slice(IReadOnlyList<WeatherHour> hours, int startHour, int endHour)
    {
        var slice = hours.Where(h => h.Local.Hour >= startHour && h.Local.Hour < endHour).ToList();
        var title = startHour switch
        {
            6 => "Matin | 06h – 12h",
            12 => "Après-midi | 12h – 18h",
            _ => "Soirée | 18h – 00h",
        };
        return new Period(title, slice);
    }

    private static IEnumerable<string> PeriodLines(Period period, int peakRain, int minimum)
    {
        var (emoji, label) = Condition(period.Hours);
        yield return $"{emoji} *{period.Title}*";
        if (period.Hours.Count == 0)
        {
            yield return "Prévision indisponible pour cette période.";
            yield break;
        }
        yield return $"{Whole(period.Hours.Min(h => h.Temperature))}°C – {Whole(period.Hours.Max(h => h.Temperature))}°C · {label}";
        if (period.MaxRain == peakRain && period.MaxRain >= minimum)
            yield return $"☔ Risque de pluie jusqu'à {period.MaxRain} %";
        var window = RainWindow(period.Hours);
        if (window is not null)
            yield return window;
    }

    private static (string Emoji, string Label) Condition(IReadOnlyList<WeatherHour> hours)
    {
        if (hours.Count == 0)
            return ("☁️", "Prévision incomplète");
        if (hours.Any(h => h.Code >= 95))
            return ("⛈️", "Risque d'orages");
        if (hours.Any(h => h.WindKmh >= 40))
            return ("💨", "Vent fort");
        if (hours.Max(h => h.Temperature) >= 35)
            return ("🌡️", "Forte chaleur");
        var buckets = hours.GroupBy(Bucket).OrderByDescending(g => g.Count()).ThenByDescending(g => Severity(g.Key)).ToList();
        var top = buckets[0];
        if (top.Count() * 2 <= hours.Count)
        {
            var kinds = buckets.Select(g => g.Key).ToHashSet();
            if ((kinds.Contains("sunny") || kinds.Contains("partly")) && kinds.Contains("cloudy"))
                return ("🌤️", "Partiellement nuageux");
        }
        return top.Key switch
        {
            "storm" => ("⛈️", "Risque d'orages"),
            "rain" => ("🌧️", "Pluie"),
            "showers" => ("🌦️", "Averses possibles"),
            "partly" => ("🌤️", "Partiellement nuageux"),
            "sunny" => ("☀️", "Ensoleillé"),
            _ => ("☁️", "Nuageux"),
        };
    }

    private static string Bucket(WeatherHour hour) => hour.Code switch
    {
        >= 95 => "storm",
        80 or 81 or 82 => "showers",
        >= 51 and <= 65 => "rain",
        2 => "partly",
        0 or 1 => "sunny",
        _ => "cloudy",
    };

    private static int Severity(string bucket) => bucket switch
    {
        "storm" => 5,
        "rain" => 4,
        "showers" => 3,
        "cloudy" => 2,
        "partly" => 1,
        _ => 0,
    };

    private static string? RainWindow(IReadOnlyList<WeatherHour> hours)
    {
        var wet = hours.Where(h => h.PrecipitationMm > 0).OrderBy(h => h.Local).ToList();
        if (wet.Count == 0)
            return null;
        var drizzle = wet.All(h => h.Code is >= 51 and <= 55 && h.PrecipitationMm < 1);
        var start = wet[0].Local.Hour;
        var end = wet[^1].Local.Hour;
        if (wet.Count == 1)
            return drizzle
                ? $"Une petite bruine est prévue vers {start}h."
                : $"Pluie possible vers {start}h.";
        return drizzle
            ? $"Une petite bruine est prévue entre {start}h et {end}h."
            : $"Averses possibles entre {start}h et {end}h.";
    }

    private static string Advice(WeatherDay day, Period[] periods)
    {
        var hours = periods.SelectMany(p => p.Hours).ToList();
        var parts = new List<string>();
        if (hours.Any(h => h.Code >= 95))
            parts.Add("Des orages sont annoncés. Évitez les endroits exposés.");
        else if (hours.Any(h => h.Code is 65 or 82) || hours.Sum(h => h.PrecipitationMm) >= 5)
            parts.Add("De fortes pluies sont prévues. Prévoyez plus de temps sur la route.");
        else if (RainWindow(hours.Where(h => h.PrecipitationMm > 0 && h.Code is >= 51 and <= 55).ToList()) is { } drizzle && hours.Any(h => h.PrecipitationMm > 0))
            parts.Add(drizzle.TrimEnd('.') + ".");
        else if (hours.Any(h => h.PrecipitationMm > 0))
            parts.Add("Prenez un parapluie si vous sortez pendant la pluie.");
        else
            parts.Add("Pas de pluie importante annoncée.");

        var hottest = hours.OrderByDescending(h => h.Apparent).FirstOrDefault();
        if (hottest is not null && hottest.Apparent >= hottest.Temperature + 3)
            parts.Add($"On ressent jusqu'à {Whole(hottest.Apparent)}°C.");
        else if (day.Maximum >= 33)
            parts.Add("Il fera chaud. Pensez à boire de l'eau.");
        return string.Join(" ", parts.Distinct());
    }

    public static string Store(WeatherDay day) =>
        System.Text.Json.JsonSerializer.Serialize(new StoredForecast
        {
            LocalDate = day.LocalDate,
            Minimum = day.Minimum,
            Maximum = day.Maximum,
            ApparentMinimum = day.ApparentMinimum,
            ApparentMaximum = day.ApparentMaximum,
            Hours = day.Hours.ToList(),
        });

    public static WeatherDay? Restore(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        var stored = System.Text.Json.JsonSerializer.Deserialize<StoredForecast>(json);
        if (stored is null || stored.Hours.Count == 0)
            return null;
        return new WeatherDay(stored.LocalDate, stored.Minimum, stored.Maximum, stored.ApparentMinimum, stored.ApparentMaximum, stored.Hours);
    }

    public static int Whole(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    public static string FrenchDate(DateTime local)
    {
        string[] days = ["dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi"];
        string[] months = ["janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre", "octobre", "novembre", "décembre"];
        var day = days[(int)local.DayOfWeek];
        return $"{char.ToUpperInvariant(day[0])}{day[1..]} {local.Day} {months[local.Month - 1]} {local.Year}";
    }

    private sealed class Period(string title, List<WeatherHour> hours)
    {
        public string Title { get; } = title;
        public List<WeatherHour> Hours { get; } = hours;
        public int MaxRain => Hours.Count == 0 ? 0 : Hours.Max(h => h.RainProbability);
    }
}
