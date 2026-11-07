using System.Text;
using System.Text.RegularExpressions;
using VibeLauncher.Models;

namespace VibeLauncher.Services;

public static class LogService
{
    private static readonly object Sync = new();

    public static string WriteCrash(string source, string text)
    {
        try
        {
            Directory.CreateDirectory(Paths.Logs);
            var path = Path.Combine(Paths.Logs, $"launcher-{DateTime.Now:yyyyMMdd}.log");
            lock (Sync)
            {
                File.AppendAllText(path,
                    $"[{DateTime.Now:HH:mm:ss}] [{source}]\n{text}\n{new string('-', 80)}\n");
            }
            return path;
        }
        catch
        {
            return "";
        }
    }

    public static List<LogEntry> GetLogs()
    {
        try
        {
            return Directory.EnumerateFiles(Paths.Logs, "*.log")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => new LogEntry
                {
                    FullPath = f.FullName,
                    Name = f.Name,
                    Modified = f.LastWriteTime,
                    Size = f.Length
                })
                .ToList();
        }
        catch
        {
            return new List<LogEntry>();
        }
    }

    public static string ReadLog(string path, int maxChars = 400_000)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var len = (int)Math.Min(fs.Length, maxChars);
            var buffer = new byte[len];
            if (len < fs.Length) fs.Seek(-len, SeekOrigin.End);
            var read = fs.Read(buffer, 0, len);
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            if (len < fs.Length) text = "… (показан конец файла) …\n" + text;
            return text;
        }
        catch (Exception ex)
        {
            return "Не удалось прочитать лог: " + ex.Message;
        }
    }

    /// <summary>
    /// Mojang-конфигурация log4j пишет консоль в XML (LegacyXMLLayout).
    /// Этот конвертер превращает поток XML-событий в обычные строки лога.
    /// </summary>
    public sealed class Log4jXmlConverter
    {
        private static readonly Regex EventStart = new(@"<log4j:Event\b([^>]*)>",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex Attribute = new(@"([A-Za-z]+)=""([^""]*)""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private bool _inEvent;
        private bool _inMessage;
        private bool _inThrowable;
        private string _thread = "";
        private string _level = "INFO";
        private long _timestamp;
        private readonly StringBuilder _message = new();
        private readonly StringBuilder _throwable = new();

        public List<string> Convert(string line)
        {
            var output = new List<string>();

            var start = EventStart.Match(line);
            if (start.Success)
            {
                Reset();
                _inEvent = true;
                foreach (Match attr in Attribute.Matches(start.Groups[1].Value))
                {
                    var name = attr.Groups[1].Value;
                    var value = attr.Groups[2].Value;
                    if (name.Equals("logger", StringComparison.OrdinalIgnoreCase)) { /* для полноты не выводим */ }
                    else if (name.Equals("thread", StringComparison.OrdinalIgnoreCase)) _thread = value;
                    else if (name.Equals("level", StringComparison.OrdinalIgnoreCase)) _level = value;
                    else if (name.Equals("timestamp", StringComparison.OrdinalIgnoreCase) &&
                             long.TryParse(value, out var stamp)) _timestamp = stamp;
                }

                var rest = line[(start.Index + start.Length)..];
                if (!string.IsNullOrWhiteSpace(rest)) ProcessContent(rest);
                if (_inEvent && rest.Contains("</log4j:Event>", StringComparison.Ordinal)) Emit(output);
                return output;
            }

            if (!_inEvent)
            {
                output.Add(line);
                return output;
            }

            if (line.TrimStart().StartsWith("</log4j:Event", StringComparison.Ordinal))
            {
                Emit(output);
                return output;
            }

            ProcessContent(line);
            return output;
        }

        private void ProcessContent(string line)
        {
            if (_inMessage || line.Contains("<log4j:Message>", StringComparison.Ordinal))
            {
                var text = ReadCData(line, ref _inMessage);
                if (text != null) _message.Append(text);
            }

            if (_inThrowable || line.Contains("<log4j:Throwable>", StringComparison.Ordinal))
            {
                var text = ReadCData(line, ref _inThrowable);
                if (text != null) _throwable.Append(text);
            }
        }

        private static string? ReadCData(string line, ref bool inBlock)
        {
            var start = line.IndexOf("<![CDATA[", StringComparison.Ordinal);
            if (start < 0)
            {
                if (inBlock && line.Contains("]]>", StringComparison.Ordinal))
                {
                    inBlock = false;
                    return line[..line.IndexOf("]]>", StringComparison.Ordinal)];
                }
                return inBlock ? line : null;
            }

            start += "<![CDATA[".Length;
            var end = line.IndexOf("]]>", start, StringComparison.Ordinal);
            if (end >= 0)
            {
                inBlock = false;
                return line[start..end];
            }

            inBlock = true;
            return line[start..];
        }

        private void Emit(List<string> output)
        {
            var time = _timestamp > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(_timestamp).LocalDateTime
                : DateTime.Now;

            var message = _message.ToString().TrimEnd('\r', '\n');
            var prefix = $"[{time:HH:mm:ss}] [{_thread}/{_level}]: ";
            if (message.Length > 0)
            {
                var lines = message.Split('\n');
                output.Add(prefix + lines[0].TrimEnd('\r'));
                for (var i = 1; i < lines.Length; i++) output.Add("    " + lines[i].TrimEnd('\r'));
            }

            var throwable = _throwable.ToString().TrimEnd('\r', '\n');
            if (throwable.Length > 0)
            {
                foreach (var line in throwable.Split('\n'))
                    output.Add(line.TrimEnd('\r'));
            }

            Reset();
        }

        private void Reset()
        {
            _inEvent = false;
            _inMessage = false;
            _inThrowable = false;
            _thread = "";
            _level = "INFO";
            _timestamp = 0;
            _message.Clear();
            _throwable.Clear();
        }
    }

    public static string CreateGameLogPath(string versionId)
    {
        Directory.CreateDirectory(Paths.Logs);
        var safe = Paths.SafeName(versionId);
        var path = Path.Combine(Paths.Logs, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}.log");
        var counter = 1;
        while (File.Exists(path))
            path = Path.Combine(Paths.Logs, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}-{counter++}.log");
        return path;
    }
}
