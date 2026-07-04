namespace Kinshout.ExternalImporter.Configuration;

internal static class DotEnvLoader
{
    public static void Load()
    {
        foreach (var path in CandidatePaths())
        {
            if (!File.Exists(path))
                continue;

            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                var key = line[..separator].Trim();
                var value = SanitizeEnvValue(line[(separator + 1)..].Trim().Trim('"'));
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                Environment.SetEnvironmentVariable(key, value);
            }

            return;
        }
    }

    private static string SanitizeEnvValue(string value) =>
        value.Trim().Trim('\r', '\n');

    private static IEnumerable<string> CandidatePaths()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            yield return Path.Combine(current.FullName, ".env");
            current = current.Parent;
        }
    }
}
