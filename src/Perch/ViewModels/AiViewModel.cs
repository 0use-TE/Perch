using System.Collections.ObjectModel;
using System.Net;
using System.Threading;
using System.Windows.Input;
using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Threading;
using Perch.Services;
using Perch.Services.Ai;
using Perch.Services.Ai.Agent;

namespace Perch.ViewModels;

// A stretch of a message: a paragraph, heading, quote, list item, code that was fenced with three backticks, a table or a
// rule. It lives as long as the message does and is updated in place while the answer grows, so the page only redraws what
// actually changed.
public sealed class ChatSegment : Observable
{
    private string text = "", info = "";
    private bool isError, copied;
    private int level;
    private MarkdownTable? table;
    private IReadOnlyList<MdRun> runs = [];
    private bool opened;
    private readonly DispatcherTimer copiedTimer = new() { Interval = TimeSpan.FromMilliseconds(1600) };

    public ChatSegment(MarkdownBlock block, bool isError, bool open)
    {
        Kind = block.Kind;
        copiedTimer.Tick += (_, _) => { copiedTimer.Stop(); Copied = false; };
        CopyCode = new RelayCommand(_ => _ = CopyAsync());
        Update(block, isError, open);
    }

    public BlockKind Kind { get; }
    public string Text { get => text; private set => Set(ref text, value); }
    // The language of a code block, or the marker of a list item.
    public string Info { get => info; private set => Set(ref info, value); }
    public bool IsError { get => isError; private set => Set(ref isError, value); }
    public MarkdownTable? Table { get => table; private set => Set(ref table, value); }
    // The text with its bold, italic, code and link marks worked out.
    public IReadOnlyList<MdRun> Runs { get => runs; private set => Set(ref runs, value); }
    public bool IsText => Kind == BlockKind.Text;
    public bool IsHeading => Kind == BlockKind.Heading;
    public bool IsQuote => Kind == BlockKind.Quote;
    public bool IsList => Kind == BlockKind.ListItem;
    public bool IsCode => Kind == BlockKind.Code;
    public bool IsTable => Kind == BlockKind.Table;
    public bool IsRule => Kind == BlockKind.Rule;
    public bool HasInfo => info.Length > 0;
    public double HeadingSize => level switch { 1 => 18, 2 => 16.5, 3 => 15.5, _ => 14.5 };
    public Thickness ListMargin => new(level * 16, 1, 0, 1);

    public ICommand CopyCode { get; }
    public bool Copied { get => copied; private set { if (Set(ref copied, value)) Changed(nameof(CopyLabel)); } }
    public string CopyLabel => Loc.T(copied ? "Ai.CodeCopied" : "Ai.CodeCopy");

    private async Task CopyAsync()
    {
        try
        {
            if (Ui.Clipboard is not { } clipboard) return;
            await clipboard.SetTextAsync(text);
            Copied = true;
            copiedTimer.Stop(); copiedTimer.Start();
        }
        catch (Exception) { /* the clipboard is busy; the button simply does nothing this time */ }
    }

    // open: this is the last block of an answer still being written, so a mark waiting for its end styles what follows.
    public void Update(MarkdownBlock block, bool error, bool open)
    {
        var changed = text != block.Text || opened != open || isError != error;
        Text = block.Text;
        Info = block.Info;
        IsError = error;
        if (level != block.Level) { level = block.Level; Changed(nameof(HeadingSize)); Changed(nameof(ListMargin)); }
        if (block.Table != null && !block.Table.SameAs(table)) Table = block.Table;
        if (!changed) return;
        opened = open;
        if (Kind is BlockKind.Text or BlockKind.Heading or BlockKind.Quote or BlockKind.ListItem)
            Runs = error ? [new MdRun(block.Text)] : Markdown.Inline(block.Text, open);
    }
}

// A question put to the user while the agent works: allow this action or not. The answer completes the task the agent waits on.
public sealed class ConfirmRequest : Observable
{
    private readonly TaskCompletionSource<bool> answer;
    public ConfirmRequest(string title, string detail, ToolRisk risk, TaskCompletionSource<bool> answer)
    {
        Title = title; Detail = detail; Danger = risk == ToolRisk.Dangerous; this.answer = answer;
        Allow = new RelayCommand(_ => this.answer.TrySetResult(true));
        Deny = new RelayCommand(_ => this.answer.TrySetResult(false));
    }
    public string Title { get; }
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;
    public bool Danger { get; }
    public ICommand Allow { get; }
    public ICommand Deny { get; }
}

// A step the agent took while answering (a search, a page it read), shown above the answer.
public sealed class AgentStepLine : Observable
{
    private string summary = "", args = "", output = "";
    private bool done, failed, open;
    public AgentStepLine(string label, string summary = "", bool done = false, bool failed = false)
    {
        Label = label; this.summary = summary; this.done = done; this.failed = failed;
        Toggle = new RelayCommand(_ => { if (HasPayload) Open = !Open; });
    }
    public string Label { get; }
    public string Summary { get => summary; set { if (Set(ref summary, value)) Changed(nameof(HasSummary)); } }
    public string Args { get => args; set { if (Set(ref args, value)) { Changed(nameof(HasPayload)); Changed(nameof(HasArgs)); } } }
    public string Output { get => output; set { if (Set(ref output, value)) { Changed(nameof(HasPayload)); Changed(nameof(HasOutput)); } } }
    public bool HasArgs => args.Length > 0 && args != "{}";
    public bool HasOutput => output.Length > 0;
    public bool HasPayload => HasArgs || HasOutput;
    public bool Open { get => open; set { if (Set(ref open, value)) Changed(nameof(ChevronGlyph)); } }
    public string ChevronGlyph => open ? "" : "";
    public ICommand Toggle { get; }
    public bool HasSummary => summary.Length > 0;
    public bool Done { get => done; set { if (Set(ref done, value)) Changed(nameof(Running)); } }
    public bool Running => !done;
    public bool Failed { get => failed; set => Set(ref failed, value); }
    // Saved with the conversation as one line: label, tab, summary, tab, "!" when it failed.
    public string Save() => Label + "\t" + summary + (failed ? "\t!" : "");
    public static AgentStepLine Load(string line)
    {
        var parts = line.Split('\t');
        return new AgentStepLine(parts[0], parts.Length > 1 ? parts[1] : "", true, parts.Length > 2);
    }
}

public sealed record SlashItem(string Command, string Description);

public sealed class ChoiceOption(string label, ICommand pick) : Observable
{
    private bool selected;
    public string Label { get; } = label;
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public ICommand Pick { get; } = pick;
}

public sealed class UserChoiceCard : Observable
{
    private readonly TaskCompletionSource<string> result = new();
    private bool answered;
    private string customText = "";

    public UserChoiceCard(string question, IReadOnlyList<string> options)
    {
        Question = question;
        PickOption = new RelayCommand(p => { if (p is ChoiceOption opt) Choose(opt.Label, opt); });
        SubmitCustom = new RelayCommand(_ =>
        {
            var text = customText.Trim();
            if (text.Length > 0) Choose(text, null);
        });
        foreach (var label in options) Options.Add(new ChoiceOption(label, PickOption));
    }

    public string Question { get; }
    public ObservableCollection<ChoiceOption> Options { get; } = [];
    public ICommand PickOption { get; }
    public ICommand SubmitCustom { get; }
    public bool Answered { get => answered; private set { if (Set(ref answered, value)) Changed(nameof(Pending)); } }
    public bool Pending => !answered;
    public string CustomText { get => customText; set => Set(ref customText, value ?? ""); }

    public Task<string> WaitAsync(CancellationToken cancel)
    {
        cancel.Register(() => result.TrySetCanceled(cancel));
        return result.Task;
    }

    private void Choose(string answer, ChoiceOption? selected)
    {
        if (answered) return;
        Answered = true;
        foreach (var opt in Options) opt.Selected = opt == selected;
        result.TrySetResult(answer);
    }
}

// One message in the conversation. The assistant's text (and its thinking, for models that think) grows while the
// answer is being written.
public sealed class ChatLine : Observable
{
    private string text = "", thinking = "", stats = "";
    private bool isError, thinkingOpen, working, caret;

    public ChatLine(string role, string text = "", string thinking = "", bool error = false, string? imageUrl = null)
    {
        Role = role; this.text = text; this.thinking = thinking; isError = error; ImageUrl = imageUrl;
        ToggleThinking = new RelayCommand(_ => ThinkingOpen = !ThinkingOpen);
        Steps.CollectionChanged += (_, _) => { Changed(nameof(HasSteps)); Changed(nameof(IsPending)); };
        Refresh();
    }

    public bool HasSteps => Steps.Count > 0;
    public string? ImageUrl { get; }
    public bool HasImage => !string.IsNullOrEmpty(ImageUrl);
    public string Stats { get => stats; set { if (Set(ref stats, value)) Changed(nameof(HasStats)); } }
    public bool HasStats => stats.Length > 0;

    public string Role { get; }
    public bool IsUser => Role == "user";
    public bool IsAssistant => !IsUser;
    public HorizontalAlignment Align => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public bool IsError { get => isError; set { if (Set(ref isError, value)) { Changed(nameof(IsFine)); Refresh(); } } }
    public bool IsFine => !isError;
    // A blinking cursor after the last character while the answer is being written (like the ChatGPT page).
    public bool Caret { get => caret; set { if (Set(ref caret, value)) Refresh(); } }
    public ICommand ToggleThinking { get; }
    public ObservableCollection<AgentStepLine> Steps { get; } = [];
    private ConfirmRequest? confirm;
    public ConfirmRequest? Confirm { get => confirm; set { if (Set(ref confirm, value)) Changed(nameof(HasConfirm)); } }
    public bool HasConfirm => confirm != null;

    private UserChoiceCard? choice;
    public UserChoiceCard? Choice { get => choice; set { if (Set(ref choice, value)) Changed(nameof(HasChoice)); } }
    public bool HasChoice => choice != null;

    public string Text { get => text; set { if (Set(ref text, value)) { Refresh(); Changed(nameof(HasText)); Changed(nameof(IsPending)); } } }
    public string Thinking { get => thinking; set { if (Set(ref thinking, value)) { Changed(nameof(HasThinking)); Changed(nameof(IsPending)); Changed(nameof(ThinkingShown)); } } }
    // What the thinking box displays. A thinking model can think for tens of thousands of characters, and laying all of that out again
    // for every few characters freezes the window; so while it is being written only the latest stretch is drawn, and even a finished
    // thought is capped (the full text is still saved with the conversation).
    public string ThinkingShown
    {
        get
        {
            var limit = working ? 1500 : 30000;
            if (thinking.Length <= limit) return thinking;
            var start = thinking.Length - limit;
            if (char.IsLowSurrogate(thinking[start])) start++;
            return "…" + thinking[start..];
        }
    }
    public bool HasText => text.Length > 0;
    public bool HasThinking => thinking.Length > 0;
    public bool ThinkingOpen { get => thinkingOpen; set { if (Set(ref thinkingOpen, value)) Changed(nameof(ThinkingGlyph)); } }
    public string ThinkingGlyph => thinkingOpen ? "" : ""; // chevron down / right
    // The answer is still being written and nothing has arrived yet.
    public bool Working { get => working; set { if (Set(ref working, value)) { Changed(nameof(IsPending)); Changed(nameof(ThinkingShown)); } } }
    public bool IsPending => working && text.Length == 0 && thinking.Length == 0 && Steps.Count == 0;

    // The text split into paragraphs, code and tables. The collection is kept and updated in place (see Refresh).
    public ObservableCollection<ChatSegment> Segments { get; } = [];

    private void Refresh()
    {
        var blocks = Markdown.Parse(text);
        if (caret)
        {
            if (blocks.Count > 0 && blocks[^1].Kind is BlockKind.Text or BlockKind.Heading or BlockKind.Quote or BlockKind.ListItem) blocks[^1] = blocks[^1] with { Text = blocks[^1].Text + "▍" };
            else blocks.Add(new MarkdownBlock(BlockKind.Text, "▍"));
        }
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var open = working && i == blocks.Count - 1;
            if (i < Segments.Count && Segments[i].Kind == block.Kind) Segments[i].Update(block, isError, open);
            else if (i < Segments.Count) Segments[i] = new ChatSegment(block, isError, open);
            else Segments.Add(new ChatSegment(block, isError, open));
        }
        while (Segments.Count > blocks.Count) Segments.RemoveAt(Segments.Count - 1);
    }
}

// A model the user can pick: it belongs to one of the configured servers.
public sealed record ModelChoice(string ProviderId, string ProviderName, string Model)
{
    public string Label => Model;
    public string Detail => ProviderName;
}

// One line of the model menu: a model, under its service's name when it is the first of that service.
public sealed class ModelRow : Observable
{
    private bool isCurrent;
    public ModelRow(ModelChoice choice, string header) { Choice = choice; Header = header; }
    public ModelChoice Choice { get; }
    public string Header { get; }
    public bool HasHeader => Header.Length > 0;
    public bool IsCurrent { get => isCurrent; set => Set(ref isCurrent, value); }
}

// A saved conversation in the history list.
public sealed class ConversationItem : Observable
{
    private bool isCurrent;
    public ConversationItem(ChatRecord record) => Record = record;
    public ChatRecord Record { get; }
    public string Title => Record.Title.Length > 0 ? Record.Title : Loc.T("Ai.NewTitle");
    public void TitleChanged() => Changed(nameof(Title));
    public string TimeLabel => Record.Updated.Date == DateTime.Today ? Record.Updated.ToString("HH:mm") : Loc.Date(Record.Updated, "Format.ShortDate");
    public bool IsCurrent { get => isCurrent; set => Set(ref isCurrent, value); }
}

public sealed class HistoryGroup : Observable
{
    private bool expanded = true;
    public HistoryGroup(string workspaceName, string workspacePath)
    {
        WorkspaceName = workspaceName;
        WorkspacePath = workspacePath;
        Toggle = new RelayCommand(_ => IsExpanded = !IsExpanded);
    }
    public string WorkspaceName { get; }
    public string WorkspacePath { get; }
    public ObservableCollection<ConversationItem> Items { get; } = [];
    public bool IsExpanded { get => expanded; set { if (Set(ref expanded, value)) Changed(nameof(ChevronGlyph)); } }
    public string ChevronGlyph => expanded ? "\uE70D" : "\uE76C";
    public ICommand Toggle { get; }
}

// AI chat with any OpenAI-compatible server: pick a model, keep a history of conversations, watch a thinking model
// think. The servers, and their keys, are set in Settings (the keys are stored protected on this computer only).
public sealed class WorkspaceChoice(string path, string name, bool isCurrent) : Observable
{
    private bool current = isCurrent;
    public string Path { get; } = path;
    public string Name { get; } = name;
    public bool IsCurrent { get => current; set => Set(ref current, value); }
}

public sealed class AiViewModel : Observable
{
    private const int HistoryLimit = 20; // messages sent back with each question
    private readonly Store store;
    private readonly PerchContext? perch;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly List<ChatRecord> records;
    private CancellationTokenSource? running;
    private ChatRecord? current;
    private string draft = "";
    private bool busy, historyOpen;
    private ModelChoice? selected;
    // Typewriter: the answer arrives from the server in bursts, and is shown at an even pace; the greeting on an empty
    // chat types itself out.
    private readonly DispatcherTimer typeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer caretTimer = new() { Interval = TimeSpan.FromMilliseconds(520) };
    private readonly DispatcherTimer greetTimer = new() { Interval = TimeSpan.FromMilliseconds(65) };
    private readonly System.Text.StringBuilder waiting = new();
    // Thinking arrives in tiny pieces; it is collected and added to the page a few times a second, not once per piece.
    private readonly DispatcherTimer thinkTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly System.Text.StringBuilder thinkWaiting = new();
    private readonly System.Diagnostics.Stopwatch typeClock = System.Diagnostics.Stopwatch.StartNew();
    private TimeSpan lastReveal;
    private double owed; // characters earned by the clock but not yet shown (a fraction carries over)
    private ChatLine? writing;
    private bool streamEnded;
    private TaskCompletionSource? drained;
    private string greetingFull = "", greeting = "";
    private int greetingAt;
    private string? attachedImageUrl;
    private string attachedImageLabel = "";
    private string memoryText = MemoryTool.Load();

    public ObservableCollection<ChatLine> Messages { get; } = [];
    public ObservableCollection<ConversationItem> History { get; } = [];
    public ObservableCollection<HistoryGroup> HistoryGroups { get; } = [];
    public ObservableCollection<ModelChoice> Choices { get; } = [];
    public ObservableCollection<SlashItem> SlashChoices { get; } = [];
    public ObservableCollection<AgentStepLine> LiveSteps { get; } = [];
    public ObservableCollection<string> WorkspaceEntries { get; } = [];

    public static string DefaultSandboxWorkspace => System.IO.Path.Combine(Store.DataFolder, "workspace");

    public static string EnsureSandboxWorkspace()
    {
        var dir = DefaultSandboxWorkspace;
        try { System.IO.Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    public string WorkspacePath
    {
        get => S.AiWorkspace;
        set
        {
            if (!CanChangeWorkspace) return;
            ApplyWorkspace(value ?? "", remember: true);
        }
    }

    private void ApplyWorkspace(string next, bool remember)
    {
        if (remember && !string.IsNullOrWhiteSpace(next) && System.IO.Directory.Exists(next))
        {
            S.AiWorkspaces.RemoveAll(p => string.Equals(p, next, StringComparison.OrdinalIgnoreCase));
            S.AiWorkspaces.Insert(0, next);
            if (S.AiWorkspaces.Count > 12) S.AiWorkspaces.RemoveRange(12, S.AiWorkspaces.Count - 12);
        }
        var changed = S.AiWorkspace != next;
        S.AiWorkspace = next;
        SafePathGuard.DefaultWorkspace = EffectiveWorkspace;
        if (changed || remember) store.Touch();
        RefreshWorkspaceEntries();
        RebuildWorkspaces();
        Changed(nameof(WorkspacePath));
        Changed(nameof(WorkspaceName));
        Changed(nameof(WorkspaceTip));
        Changed(nameof(HasWorkspace));
        Changed(nameof(NoRepo));
    }

    public bool CanChangeWorkspace => current == null && Messages.Count == 0;
    public ObservableCollection<WorkspaceChoice> RememberedWorkspaces { get; } = [];
    public bool HasRememberedWorkspaces => RememberedWorkspaces.Count > 0;
    public bool HasWorkspace => !string.IsNullOrWhiteSpace(S.AiWorkspace) && System.IO.Directory.Exists(S.AiWorkspace);
    public bool NoRepo => !HasWorkspace;
    public string EffectiveWorkspace => HasWorkspace ? S.AiWorkspace : EnsureSandboxWorkspace();
    public string WorkspaceName => HasWorkspace ? System.IO.Path.GetFileName(S.AiWorkspace.TrimEnd('\\', '/')) : Loc.T("Ai.NoRepo");
    public string WorkspaceTip => HasWorkspace ? S.AiWorkspace : Loc.T("Ai.NoRepo");
    public bool WorkspaceOnly
    {
        get => S.AiWorkspaceOnly;
        set
        {
            if (S.AiWorkspaceOnly == value) return;
            S.AiWorkspaceOnly = value;
            SafePathGuard.WorkspaceOnly = value;
            store.Touch();
            Changed();
        }
    }
    public bool ApprovalAsk => S.AiApproval is not ("script" or "none");
    public bool ApprovalScript => S.AiApproval == "script";
    public bool ApprovalNone => S.AiApproval == "none";

    // Granular Harness capability permissions ("allow" | "ask" | "deny")
    public bool PermFilesAllow => S.AiPermFiles == "allow";
    public bool PermFilesAsk => S.AiPermFiles is not ("allow" or "deny");
    public bool PermFilesDeny => S.AiPermFiles == "deny";

    public bool PermPerchAllow => S.AiPermPerch == "allow";
    public bool PermPerchAsk => S.AiPermPerch is not ("allow" or "deny");
    public bool PermPerchDeny => S.AiPermPerch == "deny";

    public bool PermAppsAllow => S.AiPermApps == "allow";
    public bool PermAppsAsk => S.AiPermApps is not ("allow" or "deny");
    public bool PermAppsDeny => S.AiPermApps == "deny";

    public bool PermUiAllow => S.AiPermUi == "allow";
    public bool PermUiAsk => S.AiPermUi is not ("allow" or "deny");
    public bool PermUiDeny => S.AiPermUi == "deny";

    public bool PermShellAllow => S.AiPermShell == "allow";
    public bool PermShellAsk => S.AiPermShell is not ("allow" or "deny");
    public bool PermShellDeny => S.AiPermShell == "deny";

    public Settings AppSettings => S;
    public void SaveSettings() => store.Touch();

    public string Draft
    {
        get => draft;
        set
        {
            if (!Set(ref draft, value)) return;
            UpdateSlashChoices();
        }
    }
    public bool SlashOpen => SlashChoices.Count > 0;
    public bool HasLiveSteps => LiveSteps.Count > 0;
    public string MemoryText { get => memoryText; set => Set(ref memoryText, value ?? ""); }
    public bool HasAttachedImage => !string.IsNullOrEmpty(attachedImageUrl);
    public string AttachedImageLabel => attachedImageLabel;
    public string GreetingText { get => greeting; private set => Set(ref greeting, value); }
    public bool IsBusy { get => busy; private set { if (Set(ref busy, value)) Changed(nameof(IsIdle)); } }
    public bool IsIdle => !busy;
    public bool NoMessages => Messages.Count == 0;
    public bool HasMessages => Messages.Count > 0;
    public bool NoHistory => History.Count == 0;
    public bool HistoryOpen { get => historyOpen; set => Set(ref historyOpen, value); }
    public string Title => current is { Title.Length: > 0 } ? current.Title : Loc.T("Ai.NewTitle");

    // The model used for the next question (and remembered between runs).
    public ModelChoice? Selected
    {
        get => selected;
        set
        {
            if (value == null || !Set(ref selected, value)) return;
            var s = store.State.Settings;
            s.AiProviderId = value.ProviderId; s.AiModel = value.Model;
            store.Touch();
            Changed(nameof(NeedsSetup)); Changed(nameof(HasKey));
            SelectionChanged();
        }
    }
    public bool HasChoices => Choices.Count > 0;

    // ---- the model menu and the "deep thinking" switch
    // DeepSeek offers the same model with and without thinking as two names (deepseek-chat / deepseek-reasoner); for such
    // a service the menu lists the model once and the switch next to it picks the thinking one.
    public ObservableCollection<ModelRow> MenuRows { get; } = [];
    public ICommand PickModel { get; }
    // Raised after a model was picked from the menu (the page closes it).
    public event Action? ModelPicked;

    private static bool IsThinkingName(string model) => model.Contains("reasoner", StringComparison.OrdinalIgnoreCase);
    private static string PlainName(string model) => model.Replace("reasoner", "chat", StringComparison.OrdinalIgnoreCase);
    private ModelChoice? Twin(ModelChoice choice, bool thinking) =>
        Choices.FirstOrDefault(c => c.ProviderId == choice.ProviderId && IsThinkingName(c.Model) == thinking
            && (thinking ? PlainName(c.Model) == PlainName(choice.Model) : c.Model == PlainName(choice.Model)));

    // Web search: the agent may search the web and read pages while answering.
    public bool WebSearch
    {
        get => S.WebSearch;
        set
        {
            if (S.WebSearch == value) return;
            S.WebSearch = value; store.Touch(); Changed();
            if (value && !WebSearchTool.Configured(S)) SettingsRequested?.Invoke(); // pages can be read, but searching needs a service
        }
    }

    // Computer tools (create folders, open apps, run scripts): the same switch as in Settings.
    public bool LocalTools
    {
        get => S.AiLocalTools;
        set { if (S.AiLocalTools == value) return; S.AiLocalTools = value; store.Touch(); Changed(); }
    }

    public bool CanThink => selected != null && Twin(selected, !IsThinkingName(selected.Model)) != null;
    public bool Thinking
    {
        get => selected != null && IsThinkingName(selected.Model);
        set { if (selected != null && Twin(selected, value) is { } twin && !Equals(twin, selected)) Selected = twin; }
    }
    // The name on the picker: the plain model even while thinking is switched on (the switch shows that).
    public string ModelLabel => selected == null ? "" : CanThink ? PlainName(selected.Model) : selected.Model;

    private void SelectionChanged()
    {
        foreach (var row in MenuRows) row.IsCurrent = selected != null && (Equals(row.Choice, selected) || Equals(row.Choice, Twin(selected, false)));
        Changed(nameof(CanThink)); Changed(nameof(Thinking)); Changed(nameof(ModelLabel));
    }

    private void RebuildMenu()
    {
        MenuRows.Clear();
        string? last = null;
        foreach (var choice in Choices)
        {
            if (IsThinkingName(choice.Model) && Twin(choice, false) != null) continue; // listed once, as its plain twin
            MenuRows.Add(new ModelRow(choice, choice.ProviderId == last ? "" : choice.ProviderName));
            last = choice.ProviderId;
        }
        SelectionChanged();
    }
    public bool NoChoices => Choices.Count == 0;
    // A key is needed unless the server is a local one (Ollama, LM Studio), which usually has none.
    public bool NeedsSetup => selected == null || (string.IsNullOrWhiteSpace(KeyFor(selected)) && !OpenAiClient.IsLocal(Provider(selected)?.BaseUrl ?? ""));
    public bool HasKey => !NeedsSetup;

    public ICommand Send { get; }
    public ICommand Stop { get; }
    public ICommand NewChat { get; }
    public ICommand ToggleHistory { get; }
    public ICommand OpenChat { get; }
    public ICommand DeleteChat { get; }
    public ICommand ClearHistory { get; }
    public ICommand OpenSettings { get; }
    public ICommand BeginRename { get; }
    public ICommand ClearAttachment { get; }
    public ICommand CaptureAttachment { get; }
    public ICommand ApplySlash { get; }
    public ICommand SaveMemory { get; }
    public ICommand ClearMemory { get; }
    public ICommand SetApproval { get; }
    public ICommand SetPerm { get; }
    public ICommand ClearWorkspace { get; }
    public ICommand SelectWorkspace { get; }
    public ICommand RemoveWorkspace { get; }
    public ICommand NewChatInWorkspace { get; }
    // Raised when the user wants to capture a screen region into the chat.
    public event Action? CaptureRequested;
    // Raised when the title box opens, so the page can focus it.
    public event Action? RenameStarted;
    private bool renaming;
    private string renameText = "";
    public bool IsRenaming { get => renaming; private set { if (Set(ref renaming, value)) Changed(nameof(NotRenaming)); } }
    public bool NotRenaming => !renaming;
    public bool CanRename => current != null;
    public string RenameText { get => renameText; set => Set(ref renameText, value); }
    // Enter or leaving the box keeps the new name (an empty one keeps the old); Escape drops it.
    public void CommitRename()
    {
        if (!renaming) return;
        IsRenaming = false;
        var name = renameText.Trim();
        if (current == null || name.Length == 0 || name == current.Title) return;
        current.Title = name.Length > 60 ? name[..60] : name;
        Changed(nameof(Title));
        Changed(nameof(CanRename));
        History.FirstOrDefault(h => h.Record == current)?.TitleChanged();
        SaveSoon();
    }
    public void CancelRename() => IsRenaming = false;
    // Raised whenever the list should scroll to its end (true = new turn or switched chat; false = streaming growth).
    public event Action<bool>? Scroll;
    // Raised when a question is sent, so the input box can release its editing lock.
    public event Action? Sent;
    // The user wants the AI page of the settings window (to add a server or a key).
    public event Action? SettingsRequested;
    // Raised when a reply completes (used by the compact island to notify if collapsed).
    public event Action? ReplyFinished;

    public AiViewModel(Store store, PerchContext? perch = null)
    {
        this.store = store;
        this.perch = perch;
        records = ChatHistory.Load();
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); var snapshot = records.ToList(); Task.Run(() => ChatHistory.Save(snapshot)); };
        typeTimer.Tick += (_, _) => Reveal();
        thinkTimer.Tick += (_, _) => FlushThinking();
        caretTimer.Tick += (_, _) => { if (writing != null && writing.Text.Length > 0) writing.Caret = !writing.Caret; };
        greetTimer.Tick += (_, _) => GreetStep();
        Send = new RelayCommand(_ => _ = AskAsync());
        Stop = new RelayCommand(_ => running?.Cancel());
        NewChat = new RelayCommand(_ => { StartNew(); HistoryOpen = false; });
        ToggleHistory = new RelayCommand(_ => HistoryOpen = !HistoryOpen);
        OpenChat = new RelayCommand(p => { if (p is ConversationItem item) Open(item.Record); });
        DeleteChat = new RelayCommand(p => { if (p is ConversationItem item) Delete(item.Record); });
        ClearHistory = new RelayCommand(_ => { records.Clear(); StartNew(); SaveSoon(); RebuildHistory(); });
        OpenSettings = new RelayCommand(_ => SettingsRequested?.Invoke());
        ClearAttachment = new RelayCommand(_ => { attachedImageUrl = null; attachedImageLabel = ""; Changed(nameof(HasAttachedImage)); Changed(nameof(AttachedImageLabel)); });
        CaptureAttachment = new RelayCommand(_ => CaptureRequested?.Invoke());
        ApplySlash = new RelayCommand(p =>
        {
            if (p is not SlashItem item) return;
            Draft = item.Command;
            SlashChoices.Clear();
            Changed(nameof(SlashOpen));
            if (item.Command is "/new" or "/daily" or "/ports" or "/memory") _ = AskAsync();
        });
        SaveMemory = new RelayCommand(_ =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(Store.DataFolder);
                var text = memoryText.Trim();
                if (text.Length > 2500) text = text[..2500];
                System.IO.File.WriteAllText(MemoryTool.FilePath, text);
                memoryText = text;
                Changed(nameof(MemoryText));
            }
            catch { }
        });
        ClearMemory = new RelayCommand(_ =>
        {
            try { if (System.IO.File.Exists(MemoryTool.FilePath)) System.IO.File.Delete(MemoryTool.FilePath); } catch { }
            MemoryText = "";
        });
        LiveSteps.CollectionChanged += (_, _) => Changed(nameof(HasLiveSteps));
        SetApproval = new RelayCommand(p =>
        {
            if (p is not string mode || mode is not ("ask" or "script" or "none")) return;
            S.AiApproval = mode;
            store.Touch();
            Changed(nameof(ApprovalAsk)); Changed(nameof(ApprovalScript)); Changed(nameof(ApprovalNone));
        });
        SetPerm = new RelayCommand(p =>
        {
            if (p is not string spec) return;
            var parts = spec.Split('|', 2);
            if (parts.Length != 2 || parts[1] is not ("allow" or "ask" or "deny")) return;
            switch (parts[0])
            {
                case "files": S.AiPermFiles = parts[1]; Changed(nameof(PermFilesAllow)); Changed(nameof(PermFilesAsk)); Changed(nameof(PermFilesDeny)); break;
                case "perch": S.AiPermPerch = parts[1]; Changed(nameof(PermPerchAllow)); Changed(nameof(PermPerchAsk)); Changed(nameof(PermPerchDeny)); break;
                case "apps": S.AiPermApps = parts[1]; Changed(nameof(PermAppsAllow)); Changed(nameof(PermAppsAsk)); Changed(nameof(PermAppsDeny)); break;
                case "ui": S.AiPermUi = parts[1]; Changed(nameof(PermUiAllow)); Changed(nameof(PermUiAsk)); Changed(nameof(PermUiDeny)); break;
                case "shell": S.AiPermShell = parts[1]; Changed(nameof(PermShellAllow)); Changed(nameof(PermShellAsk)); Changed(nameof(PermShellDeny)); break;
                default: return;
            }
            store.Touch();
        });
        ClearWorkspace = new RelayCommand(_ => WorkspacePath = "");
        SelectWorkspace = new RelayCommand(p => { if (p is string path) WorkspacePath = path; });
        NewChatInWorkspace = new RelayCommand(p =>
        {
            var path = p is HistoryGroup g ? g.WorkspacePath : p as string ?? "";
            StartNew();
            ApplyWorkspace(path, remember: false);
        });
        RemoveWorkspace = new RelayCommand(p =>
        {
            var path = p switch
            {
                WorkspaceChoice wc => wc.Path,
                HistoryGroup hg => hg.WorkspacePath,
                string s => s,
                _ => null
            };
            if (string.IsNullOrEmpty(path)) return;
            S.AiWorkspaces.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
            if (CanChangeWorkspace && string.Equals(S.AiWorkspace, path, StringComparison.OrdinalIgnoreCase))
                ApplyWorkspace("", remember: false);
            else
            {
                store.Touch();
                RebuildWorkspaces();
            }
            RebuildHistory();
        });
        if (!string.IsNullOrWhiteSpace(S.AiWorkspace) && System.IO.Directory.Exists(S.AiWorkspace)
            && !S.AiWorkspaces.Any(x => string.Equals(x, S.AiWorkspace, StringComparison.OrdinalIgnoreCase)))
            S.AiWorkspaces.Insert(0, S.AiWorkspace);
        SafePathGuard.DefaultWorkspace = EffectiveWorkspace;
        SafePathGuard.WorkspaceOnly = S.AiWorkspaceOnly;
        RefreshWorkspaceEntries();
        RebuildWorkspaces();
        PickModel = new RelayCommand(p =>
        {
            if (p is not ModelRow row) return;
            // Keep thinking on when switching between models that both have a thinking twin.
            var keepThinking = Thinking;
            Selected = row.Choice;
            if (keepThinking && CanThink) Thinking = true;
            ModelPicked?.Invoke();
        });
        BeginRename = new RelayCommand(_ => { if (current == null) return; RenameText = current.Title; IsRenaming = true; RenameStarted?.Invoke(); });
        Messages.CollectionChanged += (_, _) => { Changed(nameof(NoMessages)); Changed(nameof(HasMessages)); };
        History.CollectionChanged += (_, _) => Changed(nameof(NoHistory));
        Choices.CollectionChanged += (_, _) => { Changed(nameof(HasChoices)); Changed(nameof(NoChoices)); };
        RefreshProviders();
        RebuildHistory();
    }

    private Settings S => store.State.Settings;
    private AiProvider? Provider(ModelChoice? choice) => choice == null ? null : S.AiProviders.FirstOrDefault(p => p.Id == choice.ProviderId);

    // The key of a server. DeepSeek also reads the developer's ".env" / environment variable, so a build run from the
    // project works before anything is configured.
    private string KeyFor(ModelChoice? choice)
    {
        var provider = Provider(choice);
        if (provider == null) return "";
        var key = provider.ApiKey;
        return key.Length > 0 || provider.Id != AiProvider.DeepSeekId ? key : AiConfig.Key ?? "";
    }

    public void Relocalize() { Changed(string.Empty); RebuildHistory(); StartGreeting(); }

    // The page has just come on screen: an empty chat greets by typing the line out.
    public void OnShown() { if (NoMessages) StartGreeting(); }

    private void StartGreeting()
    {
        greetTimer.Stop();
        greetingFull = Loc.T("Ai.Greeting");
        greetingAt = 0;
        GreetingText = "\u258D";
        greetTimer.Interval = TimeSpan.FromMilliseconds(260); // a short beat before the first letter
        greetTimer.Start();
    }

    private void GreetStep()
    {
        greetTimer.Interval = TimeSpan.FromMilliseconds(62);
        if (greetingAt >= greetingFull.Length) { greetTimer.Stop(); GreetingText = greetingFull; return; }
        greetingAt += char.IsHighSurrogate(greetingFull[greetingAt]) ? 2 : 1;
        greetingAt = Math.Min(greetingAt, greetingFull.Length);
        GreetingText = greetingFull[..greetingAt] + (greetingAt < greetingFull.Length ? "\u258D" : "");
        if (greetingAt >= greetingFull.Length) greetTimer.Stop();
    }

    // Shows what the server has sent so far at an even pace: a steady few characters per frame, faster when a lot is
    // waiting so the display never falls far behind.
    private void Reveal()
    {
        var line = writing;
        if (line == null || waiting.Length == 0)
        {
            if (streamEnded) { typeTimer.Stop(); drained?.TrySetResult(); }
            return;
        }
        // About 90 characters a second, faster only when a lot has piled up, so it reads like typing and never lags far behind.
        var now = typeClock.Elapsed;
        var seconds = Math.Min((now - lastReveal).TotalSeconds, 0.1);
        lastReveal = now;
        owed += (90 + waiting.Length * 0.6) * seconds;
        var count = Math.Min((int)owed, waiting.Length);
        if (count <= 0) return;
        owed -= count;
        if (count < waiting.Length && char.IsHighSurrogate(waiting[count - 1])) count++;
        count = Math.Min(count, waiting.Length);
        if (line.Text.Length == 0 && line.ThinkingOpen) line.ThinkingOpen = false; // the answer began: fold the thinking away
        line.Text += waiting.ToString(0, count);
        waiting.Remove(0, count);
        Scroll?.Invoke(false);
    }

    private void FlushThinking()
    {
        thinkTimer.Stop();
        if (writing is { } line && thinkWaiting.Length > 0)
        {
            if (!line.HasThinking) line.ThinkingOpen = true;
            line.Thinking += thinkWaiting.ToString();
            Scroll?.Invoke(false);
        }
        thinkWaiting.Clear();
    }

    // Everything still waiting appears at once (the answer was stopped, or failed).
    private void FlushWaiting()
    {
        FlushThinking();
        typeTimer.Stop();
        if (writing != null && waiting.Length > 0) { writing.Text += waiting.ToString(); waiting.Clear(); }
    }


    // The list of models follows the servers set up in Settings; called when they change and when the page is shown.
    public void RefreshProviders()
    {
        var wanted = S.AiProviders.SelectMany(p => p.Models.Select(m => new ModelChoice(p.Id, string.IsNullOrWhiteSpace(p.Name) ? p.BaseUrl : p.Name, m))).ToList();
        if (!Choices.SequenceEqual(wanted))
        {
            Choices.Clear();
            foreach (var choice in wanted) Choices.Add(choice);
        }
        var keep = Choices.FirstOrDefault(c => c.ProviderId == S.AiProviderId && c.Model == S.AiModel) ?? Choices.FirstOrDefault();
        if (!Equals(selected, keep)) { selected = keep; Changed(nameof(Selected)); }
        RebuildMenu();
        Changed(nameof(HasKey)); Changed(nameof(NeedsSetup)); Changed(nameof(LocalTools)); Changed(nameof(WebSearch));
    }

    private void StartNew()
    {
        running?.Cancel();
        current = null;
        Messages.Clear();
        StartGreeting();
        foreach (var item in History) item.IsCurrent = false;
        foreach (var g in HistoryGroups) foreach (var item in g.Items) item.IsCurrent = false;
        Changed(nameof(Title));
        Changed(nameof(CanRename));
        Changed(nameof(CanChangeWorkspace));
    }

    private void Open(ChatRecord record)
    {
        running?.Cancel();
        current = record;
        ApplyWorkspace(record.Workspace, remember: false);
        Messages.Clear();
        foreach (var m in record.Messages)
        {
            var line = new ChatLine(m.Role, m.Text, m.Thinking, m.IsError);
            foreach (var step in m.Steps.Split('\n', StringSplitOptions.RemoveEmptyEntries)) line.Steps.Add(AgentStepLine.Load(step));
            Messages.Add(line);
        }
        if (Choices.FirstOrDefault(c => c.ProviderId == record.ProviderId && c.Model == record.Model) is { } choice) Selected = choice;
        HistoryOpen = false;
        foreach (var item in History) item.IsCurrent = item.Record == record;
        foreach (var g in HistoryGroups) foreach (var item in g.Items) item.IsCurrent = item.Record == record;
        Changed(nameof(Title));
        Changed(nameof(CanRename));
        Changed(nameof(CanChangeWorkspace));
        Scroll?.Invoke(true);
    }

    private void Delete(ChatRecord record)
    {
        records.Remove(record);
        if (current == record) StartNew();
        SaveSoon();
        RebuildHistory();
    }

    public void AddWorkspace(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path)) return;
        S.AiWorkspaces.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        S.AiWorkspaces.Insert(0, path);
        if (S.AiWorkspaces.Count > 12) S.AiWorkspaces.RemoveRange(12, S.AiWorkspaces.Count - 12);
        if (CanChangeWorkspace)
            ApplyWorkspace(path, remember: false);
        else
        {
            store.Touch();
            RebuildWorkspaces();
        }
        RebuildHistory();
    }

    private void RebuildHistory()
    {
        History.Clear();
        HistoryGroups.Clear();
        var byWs = new Dictionary<string, HistoryGroup>(StringComparer.OrdinalIgnoreCase);

        HistoryGroup EnsureGroup(string key)
        {
            if (byWs.TryGetValue(key, out var g)) return g;
            var name = string.IsNullOrWhiteSpace(key) ? Loc.T("Ai.NoRepo") : System.IO.Path.GetFileName(key.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) name = key;
            g = new HistoryGroup(name, key);
            byWs[key] = g;
            HistoryGroups.Add(g);
            return g;
        }

        foreach (var record in records.OrderByDescending(r => r.Updated))
        {
            var item = new ConversationItem(record) { IsCurrent = record == current };
            History.Add(item);
            EnsureGroup(record.Workspace ?? "").Items.Add(item);
        }

        foreach (var ws in S.AiWorkspaces.Where(System.IO.Directory.Exists))
            EnsureGroup(ws);
    }

    private void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }

    public void AttachPng(byte[] png, int width, int height)
    {
        attachedImageUrl = "data:image/png;base64," + Convert.ToBase64String(png);
        attachedImageLabel = $"{Loc.T("Ai.ImageAttached")} ({width}×{height})";
        Changed(nameof(HasAttachedImage));
        Changed(nameof(AttachedImageLabel));
    }

    private async Task AskAsync()
    {
        var question = draft.Trim();
        var imageUrl = attachedImageUrl;
        if (question.Length == 0 && imageUrl == null) return;
        if (busy || selected == null) return;

        // Lightweight built-in slash commands
        if (imageUrl == null && question.StartsWith('/'))
        {
            var cmd = question.Split(' ', 2)[0].ToLowerInvariant();
            if (cmd is "/new" or "/clear") { Draft = ""; StartNew(); HistoryOpen = false; return; }
            if (cmd == "/ports") question = "Use perch_read(kind='ports') to inspect current listening ports and summarize what is running.";
            else if (cmd == "/daily") question = "Use perch_read to check my todos, notes, and alarms, then give me a concise summary of today's plan.";
            else if (cmd == "/memory") question = "Use memory(action='read') to show what you currently remember about me and my environment.";
        }
        if (question.Length == 0 && imageUrl != null) question = "请查看这张截图并说明或分析其中的内容。";

        var choice = selected;
        var provider = Provider(choice);
        var key = KeyFor(choice);
        if (provider == null || NeedsSetup) { Changed(nameof(NeedsSetup)); return; } // the page already says where to set it up

        Draft = "";
        attachedImageUrl = null;
        attachedImageLabel = "";
        Changed(nameof(HasAttachedImage));
        Changed(nameof(AttachedImageLabel));
        if (current == null)
        {
            current = new ChatRecord
            {
                Title = question.Length > 30 ? question[..30] + "…" : question,
                Workspace = HasWorkspace ? S.AiWorkspace : ""
            };
            records.Add(current);
            Changed(nameof(Title));
            Changed(nameof(CanRename));
            Changed(nameof(CanChangeWorkspace));
        }
        current.ProviderId = choice.ProviderId; current.Model = choice.Model;
        Messages.Add(new ChatLine("user", question, imageUrl: imageUrl));
        var answer = new ChatLine("assistant") { Working = true };
        Messages.Add(answer);
        LiveSteps.Clear();
        Sent?.Invoke();
        Scroll?.Invoke(true);

        using var cancel = running = new CancellationTokenSource();
        IsBusy = true;
        writing = answer; waiting.Clear(); streamEnded = false; drained = new TaskCompletionSource();
        caretTimer.Start();
        try
        {
            var tools = Tools(answer);
            await Agent.RunAsync(OpenAiClient.Normalize(provider.BaseUrl), key, choice.Model, Conversation(answer, tools), tools,
                new AnswerObserver(this, answer), cancel.Token,
                risk => S.AiApproval switch { "none" => false, "script" => risk == ToolRisk.Dangerous, _ => risk != ToolRisk.Safe },
                ToolNeedsConfirm);
            streamEnded = true;
            typeTimer.Start();
            await drained.Task.WaitAsync(cancel.Token); // the rest of the answer is typed out before the turn counts as finished
            if (answer.Text.Length == 0) answer.Text = Loc.T("Ai.Empty");
        }
        catch (OperationCanceledException)
        {
            FlushWaiting();
            answer.Text = answer.Text.Length > 0 ? answer.Text + "  " + Loc.T("Ai.Stopped") : Loc.T("Ai.Stopped");
        }
        catch (AiException e)
        {
            FlushWaiting();
            Log.Warn($"AI request failed: HTTP {(int?)e.Status} {e.Message}");
            Fail(answer, e.Status switch
            {
                null => Loc.F("Ai.ErrNet", e.Message),
                HttpStatusCode.Unauthorized => Loc.T("Ai.Err401"),
                HttpStatusCode.PaymentRequired => Loc.T("Ai.Err402"),
                HttpStatusCode.NotFound => Loc.F("Ai.Err404", e.Message),
                HttpStatusCode.TooManyRequests => Loc.T("Ai.Err429"),
                _ => Loc.F("Ai.ErrOther", (int?)e.Status ?? 0, e.Message),
            });
        }
        catch (Exception e)
        {
            FlushWaiting();
            Log.Warn("AI request failed", e);
            Fail(answer, Loc.F("Ai.ErrNet", e.Message));
        }
        finally
        {
            FlushThinking();
            typeTimer.Stop(); caretTimer.Stop(); answer.Caret = false; writing = null; waiting.Clear();
            running = null;
            answer.Working = false;
            answer.ThinkingOpen = false;
            IsBusy = false;
            if (current != null) { Persist(current); RebuildHistory(); }
            Scroll?.Invoke(false);
            ReplyFinished?.Invoke();
        }
    }

    // Shows a failure. What was already written stays on screen with the problem noted beneath it; only an answer that
    // never began becomes the error itself.
    private static void Fail(ChatLine answer, string message)
    {
        if (answer.Text.Length > 0) answer.Text += "\n\n⚠ " + message;
        else { answer.IsError = true; answer.Text = message; }
    }

    private void Persist(ChatRecord record)
    {
        record.Updated = DateTime.Now;
        record.Messages = Messages.Select(m => new ChatMessageRecord { Role = m.Role, Text = m.Text, Thinking = m.Thinking, IsError = m.IsError, Steps = string.Join('\n', m.Steps.Select(x => x.Save())) }).ToList();
        if (records.Count > ChatHistory.MaxChats) records.Remove(records.OrderBy(r => r.Updated).First());
        SaveSoon();
    }

    // The system instruction plus the most recent turns, without the unanswered question's empty reply or failed ones.
    // Raised while an action waits for the user's OK (true) and when it has been answered (false): the island stays open meanwhile.
    public event Action<bool>? ConfirmChanged;

    // The tools offered for this answer: web tools when "Web" is on, computer/Perch/UIA tools when allowed in Settings.
    private List<ITool> Tools(ChatLine? answer = null)
    {
        var tools = new List<ITool>();
        if (answer != null)
            tools.Add(new AskUserTool((q, opts, ct) => AskUserChoiceAsync(answer, q, opts, ct)));
        if (S.WebSearch)
        {
            if (WebSearchTool.Configured(S)) tools.Add(new WebSearchTool(S));
            tools.Add(new ReadPageTool());
        }
        if (S.AiLocalTools)
        {
            if (S.AiPermFiles != "deny")
                tools.AddRange([new ReadFileTool(), new ListDirTool(), new SearchFilesTool()]);
            tools.Add(new MemoryTool());
            if (perch != null)
            {
                tools.Add(new PerchReadTool(perch));
                if (S.AiPermPerch != "deny") tools.Add(new PerchActionTool(perch));
            }
#if WINDOWS
            if (S.AiPermApps != "deny") tools.AddRange([new CreateFolderTool(), new OpenAppTool()]);
            if (S.AiPermShell != "deny") tools.Add(new PowerShellTool());
            if (S.AiPermUi != "deny") tools.AddRange([new Perch.Platform.Windows.UiInspectTool(), new Perch.Platform.Windows.UiInteractTool()]);
#endif
        }
        return tools;
    }

    private bool ToolNeedsConfirm(ITool tool)
    {
        if (S.AiApproval == "none") return false;
        var name = tool.Spec.Name;
        var policy = name switch
        {
            "read_file" or "list_dir" or "grep_files" => S.AiPermFiles,
            "perch_action" => S.AiPermPerch,
            "open_app" or "create_folder" => S.AiPermApps,
            "ui_interact" => S.AiPermUi,
            "run_powershell" => S.AiPermShell,
            _ => null
        };
        if (policy == "allow") return false;
        if (policy == "ask") return S.AiApproval switch
        {
            "script" => tool.Risk == ToolRisk.Dangerous || name is "perch_action" or "open_app" or "create_folder" or "ui_interact" or "run_powershell",
            _ => true
        };
        return S.AiApproval switch
        {
            "script" => tool.Risk == ToolRisk.Dangerous,
            _ => tool.Risk != ToolRisk.Safe
        };
    }

    private async Task<string> AskUserChoiceAsync(ChatLine answer, string question, IReadOnlyList<string> options, CancellationToken cancel)
    {
        var card = new UserChoiceCard(question, options);
        Dispatcher.UIThread.Post(() =>
        {
            answer.ThinkingOpen = false;
            answer.Choice = card;
            ConfirmChanged?.Invoke(true);
            Scroll?.Invoke(false);
        });
        try
        {
            return await card.WaitAsync(cancel);
        }
        finally
        {
            Dispatcher.UIThread.Post(() => ConfirmChanged?.Invoke(false));
        }
    }

    // Reports from the agent go into the answer being written: text is typed out, thinking and steps appear as they come.
    private sealed class AnswerObserver(AiViewModel owner, ChatLine answer) : IAgentObserver
    {
        public void Text(string piece) => Dispatcher.UIThread.Post(() =>
        {
            if (!owner.typeTimer.IsEnabled) { owner.lastReveal = owner.typeClock.Elapsed; owner.owed = 0; }
            owner.waiting.Append(piece);
            owner.typeTimer.Start();
        });
        public void Thinking(string piece) => Dispatcher.UIThread.Post(() =>
        {
            owner.thinkWaiting.Append(piece);
            if (!owner.thinkTimer.IsEnabled) owner.thinkTimer.Start();
        });
        public object StepStarted(string label)
        {
            var step = new AgentStepLine(label);
            Dispatcher.UIThread.Post(() => { answer.ThinkingOpen = false; answer.Steps.Add(step); owner.LiveSteps.Add(step); owner.Scroll?.Invoke(false); });
            return step;
        }
        public void StepPayload(object step, string args, string output) => Dispatcher.UIThread.Post(() =>
        {
            if (step is not AgentStepLine line) return;
            line.Args = args.Length > 600 ? args[..600] + "…" : args;
            line.Output = output.Length > 1800 ? output[..1800] + "…" : output;
        });
        public async Task<bool> ConfirmAsync(string title, string detail, ToolRisk risk, CancellationToken cancel)
        {
            var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                answer.Confirm = new ConfirmRequest(title, detail, risk, decision);
                owner.ConfirmChanged?.Invoke(true);
                owner.Scroll?.Invoke(false);
            });
            using var stop = cancel.Register(() => decision.TrySetResult(false));
            var allowed = await decision.Task;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                answer.Confirm = null;
                owner.ConfirmChanged?.Invoke(false);
            });
            return allowed && !cancel.IsCancellationRequested;
        }

        public void Truncated() => Dispatcher.UIThread.Post(() =>
        {
            owner.waiting.Append("\n\n" + Loc.T("Ai.Truncated"));
            owner.typeTimer.Start();
        });

        public void StepDone(object step, string summary, bool failed) => Dispatcher.UIThread.Post(() =>
        {
            if (step is not AgentStepLine line) return;
            line.Summary = summary; line.Failed = failed; line.Done = true;
        });

        public void Finished(TimeSpan elapsed, int toolCalls, int totalTokens) => Dispatcher.UIThread.Post(() =>
        {
            var parts = new List<string> { $"{elapsed.TotalSeconds:0.1}s" };
            if (toolCalls > 0) parts.Add($"{toolCalls} tools");
            if (totalTokens > 0) parts.Add($"{totalTokens:N0} tok");
            answer.Stats = string.Join(" · ", parts);
            owner.MemoryText = MemoryTool.Load();
        });
    }

    private void UpdateSlashChoices()
    {
        SlashChoices.Clear();
        if (draft.StartsWith('/') && !draft.Contains(' ') && !draft.Contains('\n'))
        {
            SlashItem[] all =
            [
                new("/daily", Loc.T("Ai.SlashDaily")),
                new("/ports", Loc.T("Ai.SlashPorts")),
                new("/memory", Loc.T("Ai.SlashMemory")),
                new("/new", Loc.T("Ai.SlashNew")),
            ];
            foreach (var item in all)
                if (item.Command.StartsWith(draft, StringComparison.OrdinalIgnoreCase))
                    SlashChoices.Add(item);
        }
        Changed(nameof(SlashOpen));
    }

    private List<ChatMessage> Conversation(ChatLine pending, List<ITool> tools)
    {
        var prompt = "You are the assistant built into Perch, a small utility on the user's screen. Answer briefly and clearly, "
            + "in the language the user writes in. Today is " + DateTime.Now.ToString("yyyy-MM-dd dddd") + ".";
        if (tools.Any(t => t is ReadPageTool))
            prompt += " You can use tools: web_search to look things up and read_webpage to open a page. Use them when the question "
                + "needs current, local or specific facts; skip them when you already know the answer. After using them, answer "
                + "from what you found and end with the sources you relied on, one link per line.";
        if (tools.Any(t => t is PerchReadTool))
            prompt += " You can inspect and control Perch itself using perch_read (todos, notes, alarms, focus timer, clipboard, listening ports, environment variables) and perch_action (add/complete todo, create note, add alarm, control focus timer).";
        if (tools.Any(t => t is ReadFileTool))
            prompt += " Prefer read_file, list_dir, grep_files and memory (persistent cross-session user memory) before falling back to run_powershell.";
        if (tools.Any(t => t is CreateFolderTool))
            prompt += " You can also act on the user's Windows computer: create_folder, open_app, run_powershell, ui_inspect (Windows UI Automation accessibility tree), and ui_interact (click/type in accessible apps). The user is asked to allow "
                + "each action before it runs; if they decline, do not try again, just say what you could not do. Use full paths. "
                + $"Home: {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}; Desktop: {Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)}; "
                + $"Documents: {Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}; Downloads: {System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")}. "
                + "Prefer the simplest tool: perch_read/perch_action for Perch items, read_file/list_dir/grep_files for files, open_app for opening things, create_folder for folders, ui_inspect/ui_interact for UI automation, run_powershell only for what they cannot do. "
                + "Afterwards say briefly what you did.";
        var memory = MemoryTool.Load();
        if (memory.Length > 0)
            prompt += "\n\nPersistent user memory:\n" + memory;
        if (HasWorkspace)
            prompt += $"\n\nActive workspace directory: {S.AiWorkspace} (relative paths in read_file/list_dir/grep_files resolve from here).";

        var list = new List<ChatMessage> { new("system", prompt) };
        var valid = Messages.Where(m => m != pending && !m.IsError && (m.Text.Length > 0 || m.HasImage)).ToList();
        var totalChars = valid.Sum(m => m.Text.Length);
        if (valid.Count > HistoryLimit || totalChars > 24_000)
        {
            var keep = Math.Min(12, valid.Count);
            var older = valid.Take(valid.Count - keep);
            var summary = string.Join("\n", older.Select(m =>
            {
                var snippet = m.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (snippet.Length > 140) snippet = snippet[..140] + "…";
                return $"{m.Role}: {snippet}";
            }));
            if (summary.Length > 3000) summary = summary[^3000..];
            if (summary.Length > 0)
                list.Add(new ChatMessage("system", "Earlier conversation summary:\n" + summary));
            valid = valid.Skip(valid.Count - keep).ToList();
        }
        // Only send the latest image as multimodal payload so older turns don't bloat requests.
        var lastImgIndex = valid.FindLastIndex(m => m.HasImage);
        for (var i = 0; i < valid.Count; i++)
        {
            var m = valid[i];
            list.Add(new ChatMessage(m.Role, m.Text, ImageDataUrl: i == lastImgIndex ? m.ImageUrl : null));
        }
        return list;
    }

    public void RefreshWorkspaceEntries()
    {
        WorkspaceEntries.Clear();
        if (!HasWorkspace) return;
        try
        {
            foreach (var dir in System.IO.Directory.EnumerateDirectories(S.AiWorkspace).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).Take(25))
            {
                var name = System.IO.Path.GetFileName(dir);
                if (name.StartsWith('.') && name != ".github") continue;
                WorkspaceEntries.Add("📁 " + name);
            }
            foreach (var file in System.IO.Directory.EnumerateFiles(S.AiWorkspace).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(35))
                WorkspaceEntries.Add("📄 " + System.IO.Path.GetFileName(file));
        }
        catch { }
    }

    private void RebuildWorkspaces()
    {
        RememberedWorkspaces.Clear();
        foreach (var dir in S.AiWorkspaces.Where(System.IO.Directory.Exists))
        {
            var name = System.IO.Path.GetFileName(dir.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) name = dir;
            RememberedWorkspaces.Add(new WorkspaceChoice(dir, name, string.Equals(dir, S.AiWorkspace, StringComparison.OrdinalIgnoreCase)));
        }
        Changed(nameof(HasRememberedWorkspaces));
    }
}
