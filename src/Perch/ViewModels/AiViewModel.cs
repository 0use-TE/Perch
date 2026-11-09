using System.Collections.ObjectModel;
using System.Net;
using System.Threading;
using System.Windows.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Perch.Services;
using Perch.Services.Ai;
using Perch.Services.Ai.Agent;

namespace Perch.ViewModels;

// A stretch of a message: ordinary text, or code that was fenced with three backticks.
public sealed record ChatSegment(string Text, bool IsCode, bool IsError)
{
    public bool IsText => !IsCode;
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
    private string summary = "";
    private bool done, failed;
    public AgentStepLine(string label, string summary = "", bool done = false, bool failed = false) { Label = label; this.summary = summary; this.done = done; this.failed = failed; }
    public string Label { get; }
    public string Summary { get => summary; set { if (Set(ref summary, value)) Changed(nameof(HasSummary)); } }
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

// One message in the conversation. The assistant's text (and its thinking, for models that think) grows while the
// answer is being written.
public sealed class ChatLine : Observable
{
    private string text = "", thinking = "";
    private bool isError, thinkingOpen, working, caret;

    public ChatLine(string role, string text = "", string thinking = "", bool error = false)
    {
        Role = role; this.text = text; this.thinking = thinking; isError = error;
        ToggleThinking = new RelayCommand(_ => ThinkingOpen = !ThinkingOpen);
        Steps.CollectionChanged += (_, _) => { Changed(nameof(HasSteps)); Changed(nameof(IsPending)); };
    }

    public bool HasSteps => Steps.Count > 0;

    public string Role { get; }
    public bool IsUser => Role == "user";
    public bool IsAssistant => !IsUser;
    public HorizontalAlignment Align => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public bool IsError { get => isError; set { if (Set(ref isError, value)) { Changed(nameof(IsFine)); Changed(nameof(Segments)); } } }
    public bool IsFine => !isError;
    // A blinking cursor after the last character while the answer is being written (like the ChatGPT page).
    public bool Caret { get => caret; set { if (Set(ref caret, value)) Changed(nameof(Segments)); } }
    public ICommand ToggleThinking { get; }
    public ObservableCollection<AgentStepLine> Steps { get; } = [];
    private ConfirmRequest? confirm;
    public ConfirmRequest? Confirm { get => confirm; set { if (Set(ref confirm, value)) Changed(nameof(HasConfirm)); } }
    public bool HasConfirm => confirm != null;

    public string Text { get => text; set { if (Set(ref text, value)) { Changed(nameof(Segments)); Changed(nameof(HasText)); Changed(nameof(IsPending)); } } }
    public string Thinking { get => thinking; set { if (Set(ref thinking, value)) { Changed(nameof(HasThinking)); Changed(nameof(IsPending)); } } }
    public bool HasText => text.Length > 0;
    public bool HasThinking => thinking.Length > 0;
    public bool ThinkingOpen { get => thinkingOpen; set { if (Set(ref thinkingOpen, value)) Changed(nameof(ThinkingGlyph)); } }
    public string ThinkingGlyph => thinkingOpen ? "" : ""; // chevron down / right
    // The answer is still being written and nothing has arrived yet.
    public bool Working { get => working; set { if (Set(ref working, value)) Changed(nameof(IsPending)); } }
    public bool IsPending => working && text.Length == 0 && thinking.Length == 0 && Steps.Count == 0;

    // The text split into ordinary and code parts, with the markdown marks that would show as stray symbols removed.
    public IReadOnlyList<ChatSegment> Segments => Split(text, isError, caret);

    private static List<ChatSegment> Split(string text, bool error, bool caret)
    {
        var parts = new List<ChatSegment>();
        var pieces = text.Split("```");
        for (var i = 0; i < pieces.Length; i++)
        {
            var piece = pieces[i];
            var code = i % 2 == 1; // between two fences; an unfinished fence counts as code while the answer is still coming
            if (code)
            {
                var lineEnd = piece.IndexOf('\n');
                if (lineEnd >= 0 && piece[..lineEnd].Trim().All(c => char.IsLetterOrDigit(c) || c is '+' or '#' or '-' or '_')) piece = piece[(lineEnd + 1)..]; // the language name after the fence
                piece = piece.TrimEnd('\n', '\r');
            }
            else piece = Plain(piece).Trim('\n', '\r');
            if (piece.Length > 0) parts.Add(new ChatSegment(piece, code, error));
        }
        if (caret)
        {
            if (parts.Count == 0) parts.Add(new ChatSegment("\u258D", false, error));
            else parts[^1] = parts[^1] with { Text = parts[^1].Text + "\u258D" };
        }
        return parts;
    }

    private static string Plain(string text)
    {
        var lines = text.Replace("**", "").Replace("__", "").Replace("`", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) lines[i] = trimmed.TrimStart('#').TrimStart();
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) lines[i] = line[..(line.Length - trimmed.Length)] + "• " + trimmed[2..];
        }
        return string.Join('\n', lines);
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

// AI chat with any OpenAI-compatible server: pick a model, keep a history of conversations, watch a thinking model
// think. The servers, and their keys, are set in Settings (the keys are stored protected on this computer only).
public sealed class AiViewModel : Observable
{
    private const int HistoryLimit = 20; // messages sent back with each question
    private readonly Store store;
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
    private readonly System.Diagnostics.Stopwatch typeClock = System.Diagnostics.Stopwatch.StartNew();
    private TimeSpan lastReveal;
    private double owed; // characters earned by the clock but not yet shown (a fraction carries over)
    private ChatLine? writing;
    private bool streamEnded;
    private TaskCompletionSource? drained;
    private string greetingFull = "", greeting = "";
    private int greetingAt;

    public ObservableCollection<ChatLine> Messages { get; } = [];
    public ObservableCollection<ConversationItem> History { get; } = [];
    public ObservableCollection<ModelChoice> Choices { get; } = [];

    public string Draft { get => draft; set => Set(ref draft, value); }
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
    // Raised whenever the list should scroll to its end (a message was added or grew).
    public event Action? Scroll;
    // The user wants the AI page of the settings window (to add a server or a key).
    public event Action? SettingsRequested;

    public AiViewModel(Store store)
    {
        this.store = store;
        records = ChatHistory.Load();
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); ChatHistory.Save(records); };
        typeTimer.Tick += (_, _) => Reveal();
        caretTimer.Tick += (_, _) => { if (writing != null) writing.Caret = !writing.Caret; };
        greetTimer.Tick += (_, _) => GreetStep();
        Send = new RelayCommand(_ => _ = AskAsync());
        Stop = new RelayCommand(_ => running?.Cancel());
        NewChat = new RelayCommand(_ => { StartNew(); HistoryOpen = false; });
        ToggleHistory = new RelayCommand(_ => HistoryOpen = !HistoryOpen);
        OpenChat = new RelayCommand(p => { if (p is ConversationItem item) Open(item.Record); });
        DeleteChat = new RelayCommand(p => { if (p is ConversationItem item) Delete(item.Record); });
        ClearHistory = new RelayCommand(_ => { records.Clear(); StartNew(); SaveSoon(); RebuildHistory(); });
        OpenSettings = new RelayCommand(_ => SettingsRequested?.Invoke());
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
        Scroll?.Invoke();
    }

    // Everything still waiting appears at once (the answer was stopped, or failed).
    private void FlushWaiting()
    {
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
        Changed(nameof(Title));
        Changed(nameof(CanRename));
    }

    private void Open(ChatRecord record)
    {
        running?.Cancel();
        current = record;
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
        Changed(nameof(Title));
        Changed(nameof(CanRename));
        Scroll?.Invoke();
    }

    private void Delete(ChatRecord record)
    {
        records.Remove(record);
        if (current == record) StartNew();
        SaveSoon();
        RebuildHistory();
    }

    private void RebuildHistory()
    {
        History.Clear();
        foreach (var record in records.OrderByDescending(r => r.Updated)) History.Add(new ConversationItem(record) { IsCurrent = record == current });
    }

    private void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }

    private async Task AskAsync()
    {
        var question = draft.Trim();
        if (question.Length == 0 || busy || selected == null) return;
        var choice = selected;
        var provider = Provider(choice);
        var key = KeyFor(choice);
        if (provider == null || NeedsSetup) { Changed(nameof(NeedsSetup)); return; } // the page already says where to set it up

        Draft = "";
        if (current == null)
        {
            current = new ChatRecord { Title = question.Length > 30 ? question[..30] + "…" : question };
            records.Add(current);
            Changed(nameof(Title));
        Changed(nameof(CanRename));
        }
        current.ProviderId = choice.ProviderId; current.Model = choice.Model;
        Messages.Add(new ChatLine("user", question));
        var answer = new ChatLine("assistant") { Working = true };
        Messages.Add(answer);
        Scroll?.Invoke();

        using var cancel = running = new CancellationTokenSource();
        IsBusy = true;
        writing = answer; waiting.Clear(); streamEnded = false; drained = new TaskCompletionSource();
        answer.Caret = true; caretTimer.Start();
        try
        {
            var tools = Tools();
            await Agent.RunAsync(OpenAiClient.Normalize(provider.BaseUrl), key, choice.Model, Conversation(answer, tools), tools,
                new AnswerObserver(this, answer), cancel.Token,
                risk => S.AiApproval switch { "none" => false, "script" => risk == ToolRisk.Dangerous, _ => risk != ToolRisk.Safe });
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
            answer.IsError = true;
            answer.Text = e.Status switch
            {
                HttpStatusCode.Unauthorized => Loc.T("Ai.Err401"),
                HttpStatusCode.PaymentRequired => Loc.T("Ai.Err402"),
                HttpStatusCode.NotFound => Loc.F("Ai.Err404", e.Message),
                HttpStatusCode.TooManyRequests => Loc.T("Ai.Err429"),
                _ => Loc.F("Ai.ErrOther", (int?)e.Status ?? 0, e.Message),
            };
        }
        catch (Exception e)
        {
            FlushWaiting();
            Log.Warn("AI request failed", e);
            answer.IsError = true;
            answer.Text = Loc.F("Ai.ErrNet", e.Message);
        }
        finally
        {
            typeTimer.Stop(); caretTimer.Stop(); answer.Caret = false; writing = null; waiting.Clear();
            running = null;
            answer.Working = false;
            answer.ThinkingOpen = false;
            IsBusy = false;
            if (current != null) { Persist(current); RebuildHistory(); }
            Scroll?.Invoke();
        }
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

    // The tools offered for this answer: web tools when "Web" is on, computer tools when allowed in Settings.
    private List<ITool> Tools()
    {
        var tools = new List<ITool>();
        if (S.WebSearch)
        {
            if (WebSearchTool.Configured(S)) tools.Add(new WebSearchTool(S));
            tools.Add(new ReadPageTool());
        }
        if (S.AiLocalTools && OperatingSystem.IsWindows()) tools.AddRange([new CreateFolderTool(), new OpenAppTool(), new PowerShellTool()]);
        return tools;
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
            if (!answer.HasThinking) answer.ThinkingOpen = true;
            answer.Thinking += piece;
            owner.Scroll?.Invoke();
        });
        public object StepStarted(string label)
        {
            var step = new AgentStepLine(label);
            Dispatcher.UIThread.Post(() => { answer.ThinkingOpen = false; answer.Steps.Add(step); owner.Scroll?.Invoke(); });
            return step;
        }
        public async Task<bool> ConfirmAsync(string title, string detail, ToolRisk risk, CancellationToken cancel)
        {
            var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                answer.Confirm = new ConfirmRequest(title, detail, risk, decision);
                owner.ConfirmChanged?.Invoke(true);
                owner.Scroll?.Invoke();
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

        public void StepDone(object step, string summary, bool failed) => Dispatcher.UIThread.Post(() =>
        {
            if (step is not AgentStepLine line) return;
            line.Summary = summary; line.Failed = failed; line.Done = true;
        });
    }

    private List<ChatMessage> Conversation(ChatLine pending, List<ITool> tools)
    {
        var prompt = "You are the assistant built into Perch, a small utility on the user's screen. Answer briefly and clearly, "
            + "in the language the user writes in. Today is " + DateTime.Now.ToString("yyyy-MM-dd dddd") + ".";
        if (tools.Any(t => t is ReadPageTool))
            prompt += " You can use tools: web_search to look things up and read_webpage to open a page. Use them when the question "
                + "needs current, local or specific facts; skip them when you already know the answer. After using them, answer "
                + "from what you found and end with the sources you relied on, one link per line.";
        if (tools.Any(t => t is CreateFolderTool))
            prompt += " You can also act on the user's Windows computer: create_folder, open_app and run_powershell. The user is asked to allow "
                + "each action before it runs; if they decline, do not try again, just say what you could not do. Use full paths. "
                + $"Home: {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}; Desktop: {Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)}; "
                + $"Documents: {Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}; Downloads: {System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")}. "
                + "Prefer the simplest tool: open_app for opening things, create_folder for folders, run_powershell only for what they cannot do. "
                + "Afterwards say briefly what you did.";
        var list = new List<ChatMessage> { new("system", prompt) };
        var history = Messages.Where(m => m != pending && !m.IsError && m.Text.Length > 0).TakeLast(HistoryLimit);
        list.AddRange(history.Select(m => new ChatMessage(m.Role, m.Text)));
        return list;
    }
}
