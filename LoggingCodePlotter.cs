using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SLS4All.Compact.Diagnostics;
using SLS4All.Compact.Graphics;
using SLS4All.Compact.IO;
using SLS4All.Compact.Printer;
using SLS4All.Compact.Slicing;

namespace Inova.ApiPlugin;

// Decorator wrapping the firmware's closed-source ImageCodePlotter. Installed via
// the firmware's [Application.PluginReplacements] mechanism (see install.sh and
// CompactServiceCollectionExtensions.cs:115). Every Process(CodeCommand) call
// the slicer makes flows through here — we tag with (layer, idx, ts), append to
// a per-layer ring buffer, and fan out to any subscribed channels. All other
// ICodePlotter members are passthrough so the existing PNG render path
// (CurrentPlotterImageGenerator → PlottedImageController) keeps working.
public sealed class LoggingCodePlotter : ICodePlotter
{
    // AQN of the firmware's real plotter. Verified via install.sh probe; if
    // it drifts in a future firmware release, AppDomain scan picks up the new
    // location automatically.
    // The type's namespace (SLS4All.Compact.Slicing) does not match its
    // assembly (SLS4All.Compact.Processing) — verified by metadata probe
    // against the deployed Inova firmware DLLs (2026-06).
    private const string ImageCodePlotterAQN =
        "SLS4All.Compact.Slicing.ImageCodePlotter, SLS4All.Compact.Processing";

    // Per-layer command history. Capped per-layer so a single dense layer
    // can't OOM us; LRU evicted across layers so a long print doesn't either.
    private const int LayerCapMax = 100_000;
    private const int LayerRetainCount = 8;

    private readonly ICodePlotter _inner;
    private readonly ILogger<LoggingCodePlotter> _logger;
    private readonly Guid _processEpoch = Guid.NewGuid();

    private readonly object _stateLock = new();
    private int _highWaterLayer = 0;
    private int _currentLayer = 0;
    private int _currentCmdIdx = 0;
    private Guid _buildEpoch;

    private readonly object _bufferLock = new();
    private readonly Dictionary<int, List<CommandFrame>> _layerBuffers = new();
    private readonly Queue<int> _layerLruOrder = new();

    private readonly ConcurrentDictionary<ChannelWriter<CommandFrame>, byte> _subscribers = new();

    public LoggingCodePlotter(IServiceProvider services, ILogger<LoggingCodePlotter> logger)
    {
        _logger = logger;
        _buildEpoch = _processEpoch;

        var innerType = ResolveImageCodePlotterType();
        try
        {
            _inner = (ICodePlotter)ActivatorUtilities.CreateInstance(services, innerType);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "LoggingCodePlotter: failed to construct inner {Type}. " +
                "Check that the parent DI container has all of ImageCodePlotter's dependencies " +
                "registered (likely ImageCodePlotterOptions and slicer-internal services).",
                innerType.AssemblyQualifiedName);
            throw;
        }

        _logger.LogInformation(
            "LoggingCodePlotter wraps {Inner} (processEpoch={Epoch})",
            innerType.AssemblyQualifiedName,
            _processEpoch);
    }

    private Type ResolveImageCodePlotterType()
    {
        var type = Type.GetType(ImageCodePlotterAQN, throwOnError: false);
        if (type is not null) return type;

        _logger.LogWarning(
            "LoggingCodePlotter: AQN '{AQN}' did not resolve directly. " +
            "Falling back to AppDomain scan.", ImageCodePlotterAQN);

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException) { continue; }

            foreach (var t in types)
            {
                if (t.Name != "ImageCodePlotter") continue;
                if (!typeof(ICodePlotter).IsAssignableFrom(t)) continue;
                _logger.LogInformation(
                    "LoggingCodePlotter: resolved ImageCodePlotter via scan: {AQN}",
                    t.AssemblyQualifiedName);
                return t;
            }
        }

        throw new InvalidOperationException(
            "LoggingCodePlotter: could not resolve ImageCodePlotter. " +
            "Check that SLS4All.Compact.Slicing.dll (or equivalent) is loaded and " +
            "matches the firmware version. AQN tried: '" + ImageCodePlotterAQN + "'.");
    }

    // --- ICodePlotter passthrough ---

    public bool OutsideDraw => _inner.OutsideDraw;
    public int LayerCount => _inner.LayerCount;
    public long Version => _inner.Version;
    public bool IsEmpty => _inner.IsEmpty;
    public int Width => _inner.Width;
    public int Height => _inner.Height;
    public SystemTimestamp[] TimestampMap => _inner.TimestampMap;

    public MimeData CreateImage(
        TimeSpan hotspotAge = default,
        SystemTimestamp? hotspotTo = default,
        string caption = "",
        int? layerIndex = null,
        int? maxSize = null,
        bool noCache = false)
        => _inner.CreateImage(hotspotAge, hotspotTo, caption, layerIndex, maxSize, noCache);

    public (int width, int height) GetMask(ref float[] output) => _inner.GetMask(ref output);

    public void ReplaceWith(float[] mask) => _inner.ReplaceWith(mask);

    public Vector2 GetCenter() => _inner.GetCenter();

    public CodePlotterMarkedObject[] GetMarkedObjects() => _inner.GetMarkedObjects();

    public MimeData CreateMarkedObjectMask(int markedObjectIndex, RgbaB on, RgbaB off)
        => _inner.CreateMarkedObjectMask(markedObjectIndex, on, off);

    // --- Intercepted members ---

    public void Clear(bool beginPrint)
    {
        if (beginPrint)
        {
            lock (_stateLock)
            {
                _buildEpoch = Guid.NewGuid();
                _highWaterLayer = 0;
                _currentLayer = 0;
                _currentCmdIdx = 0;
            }
            lock (_bufferLock)
            {
                _layerBuffers.Clear();
                _layerLruOrder.Clear();
            }
            _logger.LogInformation(
                "LoggingCodePlotter: new build epoch {Epoch}", _buildEpoch);
        }
        _inner.Clear(beginPrint);
    }

    public void Process(CodeCommand cmd)
    {
        // Forward first so _inner.LayerCount reflects post-state for the rollover
        // check. ImageCodePlotter handles its own concurrency.
        _inner.Process(cmd);

        var (op, x, y, laser, speed, raw) = ParseCommand(cmd);
        var layerCount = _inner.LayerCount;

        int layer, idx;
        Guid epoch;
        lock (_stateLock)
        {
            if (layerCount > _highWaterLayer)
            {
                _highWaterLayer = layerCount;
                _currentLayer = layerCount - 1;
                _currentCmdIdx = 0;
            }
            layer = _currentLayer;
            idx = _currentCmdIdx++;
            epoch = _buildEpoch;
        }

        var frame = new CommandFrame(
            BuildEpoch: epoch,
            Layer: layer,
            Idx: idx,
            Ts: DateTimeOffset.UtcNow,
            Op: op,
            X: x,
            Y: y,
            Laser: laser,
            Speed: speed,
            Raw: raw);

        AppendToBuffer(frame);
        FanOut(frame);
    }

    private void AppendToBuffer(CommandFrame frame)
    {
        lock (_bufferLock)
        {
            if (!_layerBuffers.TryGetValue(frame.Layer, out var list))
            {
                list = new List<CommandFrame>(1024);
                _layerBuffers[frame.Layer] = list;
                _layerLruOrder.Enqueue(frame.Layer);
                while (_layerLruOrder.Count > LayerRetainCount)
                {
                    var oldLayer = _layerLruOrder.Dequeue();
                    _layerBuffers.Remove(oldLayer);
                }
            }
            if (list.Count < LayerCapMax)
                list.Add(frame);
        }
    }

    private void FanOut(CommandFrame frame)
    {
        // ConcurrentDictionary keys are safe to enumerate without an external
        // lock. TryWrite is non-blocking; bounded subscriber channels drop
        // oldest on overflow (configured at subscriber-creation time).
        foreach (var writer in _subscribers.Keys)
        {
            writer.TryWrite(frame);
        }
    }

    // Parse op from cmd.ToString() (formatter-emitted G-code-ish), pull args
    // from CodeCommand.Arg1..Arg4 directly. ToString format is stable per
    // MovementClientBase formatters (MOVE_XY X=… Y=… RELATIVE=… SPEED=…, etc.).
    private static (string op, float? x, float? y, float? laser, float? speed, string? raw)
        ParseCommand(CodeCommand cmd)
    {
        string s = cmd.ToString();
        int space = s.IndexOf(' ');
        string op = space > 0 ? s[..space] : s;

        switch (op)
        {
            case "MOVE_XY":
                return (op, cmd.Arg1, cmd.Arg2, null, cmd.Arg4Nullable, null);
            case "SET_LASER":
                return (op, null, null, cmd.Arg1, null, null);
            case "DWELL":
            case "MOVE_Z1":
            case "MOVE_Z2":
            case "MOVE_R":
                return (op, null, null, null, null, s);
            default:
                return ("UNKNOWN", null, null, null, null, s);
        }
    }

    // --- Public-to-plugin API (called from InovaApiPlugin endpoints) ---

    public void AddSubscriber(ChannelWriter<CommandFrame> writer)
        => _subscribers.TryAdd(writer, 0);

    public void RemoveSubscriber(ChannelWriter<CommandFrame> writer)
        => _subscribers.TryRemove(writer, out _);

    // Copy-under-lock snapshot of the layer's commands from sinceIdx onward.
    // Returns an empty list if the layer isn't buffered (already evicted, or
    // never seen).
    public IReadOnlyList<CommandFrame> SnapshotLayer(int layer, int sinceIdx)
    {
        lock (_bufferLock)
        {
            if (!_layerBuffers.TryGetValue(layer, out var list)) return Array.Empty<CommandFrame>();
            if (sinceIdx <= 0) return list.ToArray();
            if (sinceIdx >= list.Count) return Array.Empty<CommandFrame>();
            var slice = new CommandFrame[list.Count - sinceIdx];
            list.CopyTo(sinceIdx, slice, 0, slice.Length);
            return slice;
        }
    }

    public PlotterState GetState()
    {
        lock (_stateLock)
        {
            return new PlotterState(_currentLayer, _currentCmdIdx, _buildEpoch);
        }
    }
}

public readonly record struct CommandFrame(
    Guid BuildEpoch,
    int Layer,
    int Idx,
    DateTimeOffset Ts,
    string Op,
    float? X,
    float? Y,
    float? Laser,
    float? Speed,
    string? Raw);

public readonly record struct PlotterState(int CurrentLayer, int CurrentCmdIdx, Guid BuildEpoch);
