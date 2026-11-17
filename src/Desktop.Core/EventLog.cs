using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace Desktop.Core;

public sealed record LogEntry(DateTimeOffset Time, string Level, string Message)
{
    public string Display => $"{Time:HH:mm:ss}  {Level}  {Message}";
}

public sealed class EventLog
{
    private readonly ObservableCollection<LogEntry> entries = [];
    private readonly HashSet<string> secrets = [];
    public ReadOnlyObservableCollection<LogEntry> Entries { get; }

    public EventLog() => Entries = new(entries);

    public void RegisterSecret(string value)
    {
        if (!string.IsNullOrEmpty(value)) secrets.Add(value);
    }

    public void Write(string level, string message)
    {
        foreach (var secret in secrets.OrderByDescending(value => value.Length))
            message = message.Replace(secret, "[已隐藏]", StringComparison.Ordinal);
        message = Regex.Replace(message, @"(?i)Bearer\s+[^\s,;]+", "Bearer [已隐藏]");
        entries.Add(new(DateTimeOffset.Now, level, message));
        while (entries.Count > 50) entries.RemoveAt(0);
    }

    public void Clear() => entries.Clear();
}
