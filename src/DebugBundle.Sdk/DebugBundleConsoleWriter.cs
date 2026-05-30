using System.Text;

namespace DebugBundle;

internal sealed class DebugBundleConsoleWriter : TextWriter
{
    private readonly TextWriter _inner;
    private readonly DebugBundleLogLevel _level;
    private readonly Func<IDebugBundleClient?> _clientAccessor;
    private readonly AsyncLocal<bool> _guard = new();

    public DebugBundleConsoleWriter(TextWriter inner, DebugBundleLogLevel level, Func<IDebugBundleClient?> clientAccessor)
    {
        _inner = inner;
        _level = level;
        _clientAccessor = clientAccessor;
    }

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value) => _inner.Write(value);

    public override void Write(string? value) => _inner.Write(value);

    public override void WriteLine(string? value)
    {
        _inner.WriteLine(value);
        Capture(value);
    }

    public override Task WriteLineAsync(string? value)
    {
        Capture(value);
        return _inner.WriteLineAsync(value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Flush();
        }

        base.Dispose(disposing);
    }

    private void Capture(string? value)
    {
        if (_guard.Value || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _guard.Value = true;
        try
        {
            _clientAccessor()?.CaptureLog(value, _level, new Dictionary<string, object?> { ["source"] = "console" });
        }
        catch
        {
            // Console capture must never interfere with application writes.
        }
        finally
        {
            _guard.Value = false;
        }
    }
}
