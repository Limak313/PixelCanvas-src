using System.Windows.Forms;

namespace PixelCanvas;

public sealed partial class MainForm
{
    // ---------- 画布与工程状态（纯数据，不依赖控件） ----------
    private PixelGrid _grid = new(32, 32);
    private List<Layer> _layers = new();
    private HistoryBudget _historyBudget = new();
    private int _activeLayer;
    private History _history => _layers[_activeLayer].History;
    private bool _updatingLayers;
    private List<string> _palette;
    private int _currentColor;
    private string? _projectPath;
    private byte[]? _projectFingerprint;
    private bool _recoveryPending;
    private bool _dirty;
    private bool _manualZoom;
    private bool _centerCanvas;
    private readonly System.Windows.Forms.Timer _autosaveTimer = new() { Interval = 60_000 };
    private readonly string _recoveryDirectory = AppPaths.RecoveryRoot;
    private readonly string _recoveryId = Guid.NewGuid().ToString("N");
    private string RecoveryPath => Path.Combine(_recoveryDirectory, _recoveryId + ".pxproj");
    private FileStream? _recoveryLock;
    private bool _autosaveErrorShown;

    // ---------- 视图模式状态 ----------
    private bool _immersive, _leftWasVisible, _rightWasVisible;
    private FormBorderStyle _previousBorder;
    private FormWindowState _previousWindowState;

    // ---------- 动画状态 ----------
    private List<AnimationFrame> _frames = new();
    private int _activeFrame;
    private int _fps = 8;
    private bool _updatingFrames, _playing;
}
