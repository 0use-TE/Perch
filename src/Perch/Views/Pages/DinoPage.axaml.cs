using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Perch.ViewModels;

namespace Perch.Views.Pages;

// Runs the dinosaur game while the island is open on this page: one step per rendered frame, paused otherwise.
public partial class DinoPage : UserControl
{
    private DinoViewModel? model;
    private bool running;
    private TimeSpan last;

    public DinoPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (model != null) model.PropertyChanged -= OnModelChanged;
            model = DataContext as DinoViewModel;
            if (model == null) return;
            model.PropertyChanged += OnModelChanged;
            Game.Best = model.Best;
            Game.NewBest += best => model.Best = best;
            Game.Engaged += model.Engage;
            OnModelChanged(null, new PropertyChangedEventArgs(nameof(DinoViewModel.Active)));
        };
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DinoViewModel.Active) || model == null) return;
        if (model.Active) Start();
        else { Game.Game.Pause(); running = false; }
    }

    private void Start()
    {
        if (running) return;
        running = true;
        last = TimeSpan.Zero;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Frame);
    }

    private void Frame(TimeSpan now)
    {
        if (!running || model?.Active != true) { running = false; return; }
        var seconds = last == TimeSpan.Zero ? 0.016 : Math.Clamp((now - last).TotalSeconds, 0.001, 0.05);
        last = now;
        Game.Tick(seconds);
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Frame);
    }
}
