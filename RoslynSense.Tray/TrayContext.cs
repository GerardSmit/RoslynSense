namespace RoslynSense.Tray;

/// <summary>
/// The notification icon and its context menu: which solutions have a host loaded, and which
/// apps those hosts have running.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    /// <summary>Icon and tooltip refresh. The menu itself re-scans when it opens, so this only
    /// has to keep the at-a-glance state honest.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(4);

    /// <summary>
    /// How long the tray stays up with nothing to show before stepping down.
    /// </summary>
    /// <remarks>
    /// It was started by a host rather than by the user, so leaving an icon behind after the last
    /// host idles out would be litter. The grace period is what keeps it from flickering away
    /// between a host's idle exit and the next tool call that spawns one.
    /// </remarks>
    private static readonly TimeSpan IdleShutdown = TimeSpan.FromMinutes(5);

    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _poll;
    private Snapshot _snapshot = Snapshot.Empty;
    private DateTime? _emptySince;

    public TrayContext()
    {
        _icon = new NotifyIcon
        {
            Icon = TrayIcons.For(hostLoaded: false, appsRunning: false),
            Text = "RoslynSense",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _icon.ContextMenuStrip.Opening += OnMenuOpening;

        _poll = new System.Windows.Forms.Timer { Interval = (int)PollInterval.TotalMilliseconds };
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();

        Refresh();
    }

    private void Refresh()
    {
        _snapshot = SenseState.Scan();

        _icon.Icon = TrayIcons.For(_snapshot.Hosts.Count > 0, _snapshot.Apps.Count > 0);
        _icon.Text = Tooltip(_snapshot);

        if (!_snapshot.IsEmpty)
        {
            _emptySince = null;
            return;
        }

        _emptySince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _emptySince >= IdleShutdown)
            ExitThread();
    }

    /// <summary>The tooltip is capped at 63 characters by the shell, so it counts rather than lists.</summary>
    private static string Tooltip(Snapshot snapshot)
    {
        if (snapshot.Hosts.Count == 0)
            return "RoslynSense — no host running";

        string solutions = snapshot.Hosts.Count == 1
            ? snapshot.Hosts[0].SolutionName
            : $"{snapshot.Hosts.Count} solutions";
        string apps = snapshot.Apps.Count switch
        {
            0 => "",
            1 => ", 1 app running",
            _ => $", {snapshot.Apps.Count} apps running",
        };

        string text = $"RoslynSense — {solutions}{apps}";
        return text.Length <= 63 ? text : text[..60] + "...";
    }

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Refresh();
        BuildMenu(_icon.ContextMenuStrip!);
        e.Cancel = false;
    }

    private void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();

        var header = new ToolStripMenuItem(HeaderText(_snapshot)) { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        foreach (var host in _snapshot.Hosts)
            menu.Items.Add(HostItem(host));

        if (_snapshot.Apps.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Running apps") { Enabled = false });
            foreach (var app in _snapshot.Apps)
                menu.Items.Add(AppItem(app));

            if (_snapshot.Apps.Count > 1)
            {
                menu.Items.Add(new ToolStripMenuItem("Stop all apps", null,
                    (_, _) => { foreach (var app in _snapshot.Apps) SenseActions.StopApp(app); Refresh(); }));
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Refresh", null, (_, _) => Refresh()));
        menu.Items.Add(new ToolStripMenuItem("Hide icon", null, (_, _) => ExitThread()));
    }

    private static string HeaderText(Snapshot snapshot) => snapshot.Hosts.Count switch
    {
        0 => "RoslynSense — no host running",
        1 => "RoslynSense — 1 solution loaded",
        var n => $"RoslynSense — {n} solutions loaded",
    };

    private ToolStripMenuItem HostItem(HostEntry host)
    {
        var item = new ToolStripMenuItem(host.SolutionName)
        {
            ToolTipText = $"{host.SolutionPath}\nHost pid {host.Pid}, up since {host.StartedAtUtc.ToLocalTime():t}",
        };

        item.DropDownItems.Add(new ToolStripMenuItem(host.SolutionPath) { Enabled = false });
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(new ToolStripMenuItem("Open solution", null,
            (_, _) => SenseActions.Open(host.SolutionPath)));
        if (host.Directory is not null)
        {
            item.DropDownItems.Add(new ToolStripMenuItem("Open containing folder", null,
                (_, _) => SenseActions.Reveal(host.SolutionPath)));
        }
        item.DropDownItems.Add(new ToolStripMenuItem("Copy path", null,
            (_, _) => SenseActions.CopyText(host.SolutionPath)));
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(new ToolStripMenuItem("View host log", null,
            (_, _) => SenseActions.Open(host.LogPath)) { Enabled = File.Exists(host.LogPath) });
        item.DropDownItems.Add(new ToolStripMenuItem($"Stop host (pid {host.Pid})", null,
            (_, _) => { SenseActions.StopHost(host); Refresh(); }));
        return item;
    }

    private ToolStripMenuItem AppItem(AppEntry app)
    {
        string label = app.Url is { Length: > 0 } url
            ? $"{app.ProjectName} — {url}"
            : $"{app.ProjectName} (pid {app.Pid})";

        var item = new ToolStripMenuItem(label)
        {
            ToolTipText = $"{app.ProjectPath}\nPid {app.Pid}, started {app.StartedAtUtc.ToLocalTime():t}",
        };

        if (app.Url is { Length: > 0 } target)
        {
            item.DropDownItems.Add(new ToolStripMenuItem("Open in browser", null,
                (_, _) => SenseActions.Open(target)));
            item.DropDownItems.Add(new ToolStripMenuItem("Copy URL", null,
                (_, _) => SenseActions.CopyText(target)));
            item.DropDownItems.Add(new ToolStripSeparator());
        }

        string log = SenseState.OutputLogFor(app.Pid);
        item.DropDownItems.Add(new ToolStripMenuItem("View output", null,
            (_, _) => SenseActions.Open(log)) { Enabled = File.Exists(log) });
        item.DropDownItems.Add(new ToolStripMenuItem("Copy project path", null,
            (_, _) => SenseActions.CopyText(app.ProjectPath)));
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(new ToolStripMenuItem($"Stop (pid {app.Pid})", null,
            (_, _) => { SenseActions.StopApp(app); Refresh(); }));
        return item;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Stop();
            _poll.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            TrayIcons.DisposeAll();
        }
        base.Dispose(disposing);
    }
}
