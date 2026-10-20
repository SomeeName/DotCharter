using System.Text;

namespace DotCharter.Tone.Logging;

public sealed class ConsoleLogRouter
{
    private readonly IAppLogger _logger;
    private bool _installed;

    public ConsoleLogRouter(IAppLogger logger) => _logger = logger;

    public void Install()
    {
        if (_installed) return;
        var originalOut = Console.Out;
        var originalError = Console.Error;

        AppLogger.SetConsoleWriters(originalOut, originalError);

        _originalOut = originalOut;
        _originalError = originalError;
        Console.SetOut(new RoutedConsoleWriter(_logger, "stdout", originalOut));
        Console.SetError(new RoutedConsoleWriter(_logger, "stderr", originalError));
        // What Console now HANDS OUT (SetOut wraps the writer in a synchronized
        // one), so Uninstall can tell whether the stream is still ours.
        _out = Console.Out;
        _error = Console.Error;

        _installed = true;
        _logger.Info(AppLogCategory.Application,
            "Smart Console log router installed. Intercepting external logs while allowing Serilog passthrough.");
    }

    private TextWriter? _out;
    private TextWriter? _error;
    private TextWriter? _originalOut;
    private TextWriter? _originalError;

    /// <summary>
    /// Puts back the writers <see cref="Install"/> wrapped, so the process-wide
    /// <see cref="Console"/> stops rooting this router's logger. Only a host that
    /// builds more than one app per process needs it (the headless test session
    /// builds one per dispatch; every install would wrap the previous one, a
    /// chain of every logger the run ever made). Restores a stream only while it
    /// is still OURS — a later install that wrapped it keeps its own.
    /// </summary>
    public void Uninstall()
    {
        if (!_installed) return;
        if (ReferenceEquals(Console.Out, _out) && _originalOut != null) Console.SetOut(_originalOut);
        if (ReferenceEquals(Console.Error, _error) && _originalError != null) Console.SetError(_originalError);
        _out = _error = null;
        _originalOut = _originalError = null;
        _installed = false;
    }

    private sealed class RoutedConsoleWriter : TextWriter
    {
        private readonly StringBuilder _buffer = new();
        private readonly IAppLogger _logger;
        private readonly TextWriter _originalWriter;
        private readonly string _source;
        private readonly object _sync = new();

        public RoutedConsoleWriter(IAppLogger logger, string source, TextWriter originalWriter)
        {
            _logger = logger;
            _source = source;
            _originalWriter = originalWriter;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_sync)
            {
                if (value == '\r') return;
                if (value == '\n')
                {
                    FlushLine();
                    return;
                }

                _buffer.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            lock (_sync)
            {
                foreach (var ch in value)
                {
                    if (ch == '\r') continue;
                    if (ch == '\n')
                    {
                        FlushLine();
                        continue;
                    }

                    _buffer.Append(ch);
                }
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_sync)
            {
                if (!string.IsNullOrEmpty(value))
                    _buffer.Append(value);
                FlushLine();
            }
        }

        public override void Flush()
        {
            lock (_sync)
            {
                FlushLine();
            }
        }

        private void FlushLine()
        {
            var line = _buffer.ToString().Trim();
            _buffer.Clear();
            if (line.Length == 0) return;

            // THE MAGIC INFINITE LOOP BREAKER:
            // Check if the line is ALREADY formatted by Serilog.
            // Serilog outputs either an ANSI color code (\x1b) or a date starting with yyyy-MM-dd.
            var isSerilog = line.StartsWith('\x1b') ||
                            line.StartsWith('\u001b') ||
                            (line.Length >= 10 && char.IsDigit(line[0]) && line[4] == '-' && line[7] == '-');

            if (isSerilog)
            {
                // Pass directly to the real console, bypassing the router.
                _originalWriter.WriteLine(line);
                return;
            }

            // Otherwise, it's raw external native output: route it to the AppLogger.
            _logger.External(_source, line);
        }
    }
}
