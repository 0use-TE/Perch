using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Perch.Services;

namespace Perch.ViewModels;

public sealed partial class PortsViewModel
{
    private string section = "ports", envScope = "User", firewallFilter = "port", toolsSearch = "", status = "";
    private string overlay = "", editError = "";
    private bool busy, envLoaded, firewallLoaded;
    private List<EnvironmentEntry> variables = [];
    private List<FirewallEntry> rules = [];

    // Inline environment variable editor state
    private EnvironmentEntry? editingOriginal;
    private string editEnvScope = "User", editEnvName = "", editEnvValue = "";
    private bool editEnvReveal;

    // Inline firewall rule editor state
    private string ruleName = "", rulePort = "", ruleProtocol = "TCP", ruleDirection = "Inbound", ruleAction = "Allow", ruleProfile = "Private";

    // Inline confirmation state
    private string confirmTitle = "", confirmDetail = "";
    private Func<Task>? confirmAction;

    public bool WindowsTools => DesktopTools.Supported;
    public bool IsBrowsing => overlay.Length == 0;
    public bool IsEditingEnv => overlay == "env";
    public bool IsAddingRule => overlay == "rule";
    public bool IsConfirming => overlay == "confirm";

    public bool IsPorts => section == "ports";
    public bool IsEnvironment => section == "environment";
    public bool IsFirewall => section == "firewall";
    public bool UserScope => envScope == "User";
    public bool MachineScope => envScope == "Machine";
    public string EnvironmentScope => envScope;
    public bool FirewallAll => firewallFilter == "all";
    public bool FirewallPortOnly => firewallFilter == "port";
    public bool FirewallPerchOnly => firewallFilter == "perch";
    public IReadOnlyList<EnvironmentEntry> AllVariables => variables;

    public bool Busy
    {
        get => busy;
        private set
        {
            if (!Set(ref busy, value)) return;
            Changed(nameof(Ready));
            Changed(nameof(ToolsLoading));
            Changed(nameof(ShowToolsList));
            Changed(nameof(ToolsEmpty));
            Changed(nameof(HasStatusText));
        }
    }
    public bool Ready => !Busy;
    public bool ToolsLoading => busy && !IsPorts && IsBrowsing;
    public bool ShowToolsList => !ToolsLoading;
    public string Status { get => status; set { Set(ref status, value); Changed(nameof(StatusText)); Changed(nameof(HasStatusText)); } }
    public string StatusText => status;
    public bool HasStatusText => !busy && status.Length > 0;
    public string ToolsSearch { get => toolsSearch; set { if (Set(ref toolsSearch, value ?? "")) RebuildTools(); } }

    public ObservableCollection<EnvironmentEntry> Variables { get; } = [];
    public ObservableCollection<FirewallEntry> Rules { get; } = [];
    public EnvironmentEntry? SelectedVariable { get; set; }
    public FirewallEntry? SelectedRule { get; set; }
    public PortEntry? SelectedPort { get; set; }
    public bool ToolsEmpty => !busy && (IsEnvironment ? envLoaded && Variables.Count == 0 : IsFirewall && firewallLoaded && Rules.Count == 0);

    // Environment editor properties
    public string EditEnvTitle => Loc.T(editingOriginal == null ? "Tools.AddVariable" : "Tools.EditVariable");
    public bool EditEnvCanChangeScope => editingOriginal == null;
    public bool EditEnvUser => editEnvScope == "User";
    public bool EditEnvMachine => editEnvScope == "Machine";
    public string EditEnvName
    {
        get => editEnvName;
        set
        {
            if (!Set(ref editEnvName, value ?? "")) return;
            editEnvReveal = false;
            Changed(nameof(EditEnvIsPath));
            Changed(nameof(EditEnvSingleLine));
            Changed(nameof(EditEnvIsSecret));
            Changed(nameof(EditEnvMask));
            Changed(nameof(EditEnvReveal));
            Changed(nameof(HasMissingPaths));
            Changed(nameof(CleanMissingLabel));
        }
    }
    public string EditEnvValue
    {
        get => editEnvValue;
        set
        {
            if (!Set(ref editEnvValue, value ?? "")) return;
            if (EditEnvIsPath) { Changed(nameof(HasMissingPaths)); Changed(nameof(CleanMissingLabel)); }
        }
    }
    public bool EditEnvIsPath => editEnvName.Trim().Equals("PATH", StringComparison.OrdinalIgnoreCase);
    public bool EditEnvSingleLine => !EditEnvIsPath;
    public bool EditEnvIsSecret => !EditEnvIsPath && DesktopTools.IsSecret(editEnvName);
    public bool EditEnvReveal { get => editEnvReveal; set { if (Set(ref editEnvReveal, value)) Changed(nameof(EditEnvMask)); } }
    public char EditEnvMask => EditEnvIsSecret && !editEnvReveal ? '●' : '\0';
    public int MissingPathCount => !EditEnvIsPath ? 0 : editEnvValue.Replace("\r", "").Split('\n')
        .Select(s => s.Trim()).Count(s => s.Length > 0 && !DesktopTools.PathDirectoryExists(s));
    public bool HasMissingPaths => MissingPathCount > 0;
    public string CleanMissingLabel => Loc.F("Tools.CleanMissing", MissingPathCount);

    // Firewall rule editor properties
    public string RuleName { get => ruleName; set => Set(ref ruleName, value ?? ""); }
    public string RulePort { get => rulePort; set => Set(ref rulePort, value ?? ""); }
    public bool RuleTcp => ruleProtocol == "TCP";
    public bool RuleUdp => ruleProtocol == "UDP";
    public bool RuleInbound => ruleDirection == "Inbound";
    public bool RuleOutbound => ruleDirection == "Outbound";
    public bool RuleAllow => ruleAction == "Allow";
    public bool RuleBlock => ruleAction == "Block";
    public bool RulePrivate => ruleProfile == "Private";
    public bool RulePublic => ruleProfile == "Public";
    public bool RuleDomain => ruleProfile == "Domain";

    // Confirmation / error properties
    public string ConfirmTitle => confirmTitle;
    public string ConfirmDetail => confirmDetail;
    public string EditError { get => editError; private set { Set(ref editError, value); Changed(nameof(HasEditError)); } }
    public bool HasEditError => editError.Length > 0;

    public ICommand SelectSection => new RelayCommand(async p =>
    {
        if (Busy) return;
        overlay = "";
        EditError = "";
        section = p as string ?? "ports";
        toolsSearch = "";
        Status = "";
        refresh.Stop();
        if (IsPorts)
        {
            Changed(string.Empty);
            Load();
            if (active) refresh.Start();
        }
        else
        {
            await EnsureToolsLoaded();
        }
    });
    public ICommand SelectScope => new RelayCommand(p => { envScope = p as string ?? "User"; Status = ""; RebuildTools(); });
    public ICommand SetFirewallFilter => new RelayCommand(p => { firewallFilter = p as string ?? "all"; Status = ""; RebuildTools(); });
    public ICommand ReloadTools => new RelayCommand(async _ => await RefreshTools());
    public ICommand OpenAdvancedFirewall => new RelayCommand(_ => ShellActions.Open("wf.msc"));
    public ICommand CancelOverlay => new RelayCommand(_ => { if (!Busy) { overlay = ""; EditError = ""; Changed(string.Empty); } });
    public ICommand CleanMissingPaths => new RelayCommand(_ =>
    {
        if (!EditEnvIsPath) return;
        var kept = editEnvValue.Replace("\r", "").Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0 && DesktopTools.PathDirectoryExists(s));
        EditEnvValue = string.Join(Environment.NewLine, kept);
    });

    public ICommand BeginAddVariable => new RelayCommand(_ => OpenVariableEditor(null));
    public ICommand BeginEditVariable => new RelayCommand(p => OpenVariableEditor(p as EnvironmentEntry ?? SelectedVariable));
    public ICommand SetEditEnvScope => new RelayCommand(p => { if (EditEnvCanChangeScope) { editEnvScope = p as string ?? "User"; Changed(nameof(EditEnvUser)); Changed(nameof(EditEnvMachine)); } });
    public ICommand SaveVariable => new RelayCommand(async _ => await CommitVariable());
    public ICommand AskDeleteVariable => new RelayCommand(p =>
    {
        if (p is not EnvironmentEntry variable) return;
        AskConfirm(Loc.T("Tools.Delete"), Loc.F("Tools.DeleteVariableConfirm", variable.Name, variable.ScopeText), async () =>
        {
            CheckUnchanged(variable);
            await DesktopTools.SaveEnvironment(variable.Scope, variable.Name, null);
            await RefreshTools();
            Status = Loc.T("Tools.EnvironmentSaved");
        });
    });

    public ICommand BeginAddRule => new RelayCommand(p => OpenRuleEditor(p as PortEntry));
    public ICommand SetRuleOption => new RelayCommand(p =>
    {
        if (p is not string tag) return;
        var parts = tag.Split(':', 2);
        if (parts.Length != 2) return;
        switch (parts[0])
        {
            case "proto": ruleProtocol = parts[1]; break;
            case "dir": ruleDirection = parts[1]; break;
            case "act": ruleAction = parts[1]; break;
            case "prof": ruleProfile = parts[1]; break;
        }
        Changed(string.Empty);
    });
    public ICommand SaveRule => new RelayCommand(async _ => await CommitRule());
    public ICommand AskToggleRule => new RelayCommand(p =>
    {
        if (p is not FirewallEntry rule || !rule.Editable) return;
        AskConfirm(Loc.T("Tools.Toggle"), Loc.F("Tools.ToggleRuleConfirm", rule.Name, Loc.T(rule.Enabled ? "Tools.Disabled" : "Tools.Enabled")), async () =>
        {
            await DesktopTools.ChangeFirewall(rule, false);
            await RefreshTools();
        });
    });
    public ICommand AskDeleteRule => new RelayCommand(p =>
    {
        if (p is not FirewallEntry rule || !rule.Deletable) return;
        AskConfirm(Loc.T("Tools.Delete"), Loc.F("Tools.DeleteRuleConfirm", rule.Name), async () =>
        {
            await DesktopTools.ChangeFirewall(rule, true);
            await RefreshTools();
        });
    });
    public ICommand AskEndProcess => new RelayCommand(p =>
    {
        if (p is PortEntry port && port.CanTerminate) ConfirmEndProcess(port);
    });
    public ICommand AcceptConfirm => new RelayCommand(async _ => await RunConfirm());

    public void CopyVariable(EnvironmentEntry variable) => Copy(variable.Value);

    public void EnsureEnvironmentLoaded()
    {
        if (envLoaded || !WindowsTools) return;
        variables = DesktopTools.ReadEnvironment().ToList();
        envLoaded = true;
    }

    public async Task EnsureToolsLoaded()
    {
        if ((IsEnvironment && envLoaded) || (IsFirewall && firewallLoaded))
        {
            RebuildTools();
            return;
        }
        await RefreshTools();
    }

    public void OpenVariableEditor(EnvironmentEntry? variable)
    {
        if (Busy) return;
        editingOriginal = variable;
        editEnvScope = variable?.Scope ?? envScope;
        editEnvName = variable?.Name ?? "";
        editEnvValue = variable?.IsPath == true ? variable.Value.Replace(";", Environment.NewLine) : variable?.Value ?? "";
        editEnvReveal = false;
        EditError = "";
        overlay = "env";
        Changed(string.Empty);
    }

    public void OpenRuleEditor(PortEntry? port = null)
    {
        if (Busy) return;
        ruleName = port == null ? "" : $"Perch · {port.Process} · {port.Port}";
        rulePort = port?.Port.ToString() ?? "";
        ruleProtocol = "TCP";
        ruleDirection = "Inbound";
        ruleAction = "Allow";
        ruleProfile = "Private";
        EditError = "";
        overlay = "rule";
        Changed(string.Empty);
    }

    public void ConfirmEndProcess(PortEntry port)
    {
        AskConfirm(Loc.T("Tools.EndProcess"), Loc.F("Tools.EndConfirm", port.Process, port.Pid, port.Port), async () =>
        {
            await Task.Run(() => EndProcess(port));
            Load();
            Status = Loc.T("Tools.ProcessEnded");
        });
    }

    private void AskConfirm(string title, string detail, Func<Task> action)
    {
        if (Busy) return;
        confirmTitle = title;
        confirmDetail = detail;
        confirmAction = action;
        EditError = "";
        overlay = "confirm";
        Changed(string.Empty);
    }

    private async Task CommitVariable()
    {
        if (Busy) return;
        Busy = true;
        EditError = Loc.T("Tools.Working");
        try
        {
            var key = editEnvName.Trim();
            var text = key.Equals("PATH", StringComparison.OrdinalIgnoreCase) ? DesktopTools.PathFromLines(editEnvValue) : editEnvValue;
            DesktopTools.ValidateVariable(key, text);
            if (editingOriginal == null && DesktopTools.ReadEnvironment().Any(v => v.Scope == editEnvScope && v.Name.Equals(key, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(Loc.T("Tools.Exists"));
            if (editingOriginal != null) CheckUnchanged(editingOriginal);
            await DesktopTools.SaveEnvironment(editEnvScope, key, text);
            envScope = editEnvScope;
            variables = (await Task.Run(DesktopTools.ReadEnvironment)).ToList();
            envLoaded = true;
            RebuildTools();
            overlay = "";
            EditError = "";
            Status = Loc.T("Tools.EnvironmentSaved");
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { EditError = Loc.T("Tools.Cancelled"); }
        catch (Exception e) { EditError = e.Message; }
        finally { Busy = false; Changed(string.Empty); }
    }

    private async Task CommitRule()
    {
        if (Busy) return;
        Busy = true;
        EditError = Loc.T("Tools.Working");
        try
        {
            await DesktopTools.CreateFirewall(ruleName.Trim(), rulePort.Trim(), ruleProtocol, ruleDirection, ruleAction, ruleProfile);
            section = "firewall";
            rules = (await DesktopTools.ReadFirewall()).ToList();
            firewallLoaded = true;
            RebuildTools();
            overlay = "";
            EditError = "";
            Status = Loc.T("Tools.Saved");
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { EditError = Loc.T("Tools.Cancelled"); }
        catch (Exception e) { EditError = e.Message; }
        finally { Busy = false; Changed(string.Empty); }
    }

    private async Task RunConfirm()
    {
        if (Busy || confirmAction == null) return;
        Busy = true;
        EditError = Loc.T("Tools.Working");
        try
        {
            await confirmAction();
            overlay = "";
            EditError = "";
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { EditError = Loc.T("Tools.Cancelled"); }
        catch (Exception e) { EditError = e.Message; }
        finally { Busy = false; Changed(string.Empty); }
    }

    private static void CheckUnchanged(EnvironmentEntry original)
    {
        var current = DesktopTools.ReadEnvironment().FirstOrDefault(v => v.Scope == original.Scope && v.Name.Equals(original.Name, StringComparison.OrdinalIgnoreCase));
        if (current?.Value != original.Value) throw new InvalidOperationException(Loc.T("Tools.VariableChanged"));
    }

    public async Task Perform(Func<Task> operation, string success = "")
    {
        if (Busy) return;
        Busy = true;
        Changed(string.Empty);
        try { await operation(); Status = success.Length > 0 ? Loc.T(success) : ""; }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { Status = Loc.T("Tools.Cancelled"); }
        catch (Exception e) { Status = e.Message; }
        finally { Busy = false; Changed(string.Empty); }
    }

    public async Task RefreshTools()
    {
        await Perform(async () =>
        {
            if (!WindowsTools) { Status = Loc.T("Tools.WindowsOnly"); return; }
            if (IsEnvironment) { variables = (await Task.Run(DesktopTools.ReadEnvironment)).ToList(); envLoaded = true; }
            else if (IsFirewall) { rules = (await DesktopTools.ReadFirewall()).ToList(); firewallLoaded = true; }
            RebuildTools();
        });
    }

    public void RebuildTools()
    {
        bool Matches(string text) => text.Contains(toolsSearch.Trim(), StringComparison.OrdinalIgnoreCase);
        Variables.Clear();
        foreach (var item in variables.Where(v => v.Scope == envScope && (Matches(v.Name) || (!DesktopTools.IsSecret(v.Name) && Matches(v.Value)))))
            Variables.Add(item);
        Rules.Clear();
        var filteredRules = rules.Where(r => firewallFilter switch
        {
            "port" => r.HasNumericPort,
            "perch" => r.Managed,
            _ => true,
        });
        foreach (var item in filteredRules.Where(r => Matches(r.Name) || Matches(r.Id) || Matches(r.Ports) || Matches(r.Protocol) || Matches(r.Detail)))
            Rules.Add(item);
        Changed(string.Empty);
    }

    public static void EndProcess(PortEntry entry)
    {
        if (!entry.CanTerminate)
            throw new InvalidOperationException(Loc.T("Tools.ProtectedProcess"));
        using var process = Process.GetProcessById(entry.Pid);
        if (process.StartTime.ToUniversalTime() != entry.StartedUtc ||
            !(Perch.Services.Ports.PortSource.Current?.Read().Any(p => p.Pid == entry.Pid && p.Port == entry.Port) ?? false))
            throw new InvalidOperationException(Loc.T("Tools.ProcessChanged"));
        if (entry.Path != null && !string.Equals(process.MainModule?.FileName, entry.Path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.T("Tools.ProcessChanged"));
        process.Kill(); // Only the selected process; never kill its children implicitly.
    }

    public void RelatedFirewall(PortEntry entry)
    {
        firewallFilter = "port";
        SelectSection.Execute("firewall");
        ToolsSearch = entry.Port.ToString();
    }

    public void FocusPort(PortEntry entry)
    {
        overlay = "";
        section = "ports";
        filter = "all";
        Search = entry.Port.ToString();
        SelectedPort = entry;
        Changed(string.Empty);
    }

    public void FocusVariable(EnvironmentEntry entry)
    {
        overlay = "";
        section = "environment";
        envScope = entry.Scope;
        ToolsSearch = entry.Name;
        SelectedVariable = entry;
        RebuildTools();
    }
}
