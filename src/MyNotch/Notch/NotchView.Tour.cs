using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using MyNotch.Composition;
using MyNotch.Mascot;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;

namespace MyNotch.Notch;

/// <summary>
/// The tour of the notch (after the first launch's welcome, or from the settings): the notch opens and stays open, a
/// card under it says what each part does, and a ring in the mascot's colour goes round that part, springing from one
/// to the next. Most steps are tried rather than read: the card asks for the gesture (another tab, the sample file
/// dropped on the notch, the volume turned, the mascot clicked, a right click, the search's shortcut), the step waits
/// for it, says it is done and goes on by itself. Nothing runs while it waits. The sample file leads to the Drop Zone
/// as a real file would (nothing is sent); the search's shortcut opens the search for real: the tour steps aside, and
/// comes back on its next step (the assistant) once the search is closed. It ends with its last step, when skipped,
/// when the notch is put away, when something else needs the bubble, or when the mascot is clicked outside its own step
/// (the assistant then opens: the user is trying things, fine).
/// </summary>
public sealed partial class NotchView
{
    // The ring: this far outside what it rings, this thick, and its soft glow around.
    private const double RingMargin = 5;
    private const float RingThickness = 2;
    private const float RingGlowThickness = 7;

    // The little notches' mouse areas grow this much during the tour: the window's region also clips what is drawn, and
    // the ring round them must show whole.
    private const double TourRoom = 12;

    // Tried: how long « done » shows before the next step.
    private static readonly TimeSpan TourDoneFor = TimeSpan.FromMilliseconds(1600);

    private Tour? _tour;
    private bool _showingTourCard;
    private DispatcherQueueTimer? _tourTimer;
    private CompositionPropertySet? _ring;
    private SpringChannel? _ringX;
    private SpringChannel? _ringY;
    private SpringChannel? _ringWidth;
    private SpringChannel? _ringHeight;
    private SpringChannel? _ringRadius;
    private SpringChannel? _ringShown;
    private CompositionColorBrush? _ringStroke;
    private CompositionColorBrush? _ringGlow;

    /// <summary>
    /// Opens the notch on its first tab and walks through <paramref name="steps"/> (those whose part is missing left out,
    /// see <see cref="TourStep.NeedsTarget"/>). A tour already going starts over.
    /// </summary>
    public void StartTour(IReadOnlyList<TourStep> steps)
    {
        EndTour();
        var shown = steps.Where(step => !step.NeedsTarget || HasTarget(step.Target)).ToList();
        int first = _modules.FindIndex(slot => slot.Module.HasTab);
        if (shown.Count == 0 || first < 0)
        {
            return;
        }

        ShowModule(first); // The window shows first, if an app full screen had put it out of sight.
        var card = new TourCard();
        var tour = new Tour(shown, card, _controller.Hold());
        _tour = tour;
        card.NextRequested += (_, _) => Step(tour, +1);
        card.BackRequested += (_, _) => Step(tour, -1);
        card.SkipRequested += (_, _) => EndTour();
        card.ActionRequested += (_, _) =>
        {
            var action = tour.Steps[tour.Index].Action;
            EndTour();
            action?.Invoke();
        };

        // Measured before it is on screen, then again once it is (its buttons' styles applied): the bubble fits it.
        card.Loaded += (_, _) =>
        {
            if (_tour == tour)
            {
                tour.Bubble?.Resize(card.Show(tour.Steps[tour.Index], tour.Index, tour.Steps.Count, tour.Done));
            }
        };

        ShowTourCard(tour);
        UpdateSideBounds(); // Room for the ring round the little notches.
    }

    /// <summary>The tour's card, at its step, falls from the open notch; the ring goes round the step's part.</summary>
    private void ShowTourCard(Tour tour)
    {
        var step = tour.Steps[tour.Index];
        ShowStepModule(step);
        var size = tour.Card.Show(step, tour.Index, tour.Steps.Count);
        _showingTourCard = true;
        tour.Bubble = ShowBubble(tour.Card, size, null, null);
        _showingTourCard = false;
        Watch(tour);
        RingRound(step.Target, appear: true);
    }

    /// <summary>
    /// The tour stepped aside (see <see cref="StepAside"/>) and what took the notch is closed: it comes back, on the notch
    /// open again, at its next step (its step tried) or at the same one.
    /// </summary>
    private void ResumeTour(Tour tour)
    {
        if (_tour != tour || !tour.Away)
        {
            return;
        }

        tour.Away = false;
        if (tour.Advance)
        {
            if (tour.Index + 1 >= tour.Steps.Count)
            {
                EndTour();
                return;
            }

            tour.Index++;
        }

        tour.Done = false;
        tour.Hold = _controller.Hold();
        int tab = _selected < _modules.Count && _modules[_selected].Module.HasTab ? _selected : _modules.FindIndex(slot => slot.Module.HasTab);
        ShowModule(tab);
        ShowTourCard(tour);
        UpdateSideBounds();
    }

    /// <summary>Back on a tab while the tour stepped aside (the search left for a tab): it comes back.</summary>
    private void TourBackOnTab()
    {
        if (_tour is { Away: true } tour && _phase == NotchPhase.Expanded)
        {
            ResumeTour(tour);
        }
    }

    /// <summary>
    /// The tour's sample file dragged: over the open notch, the Drop Zone takes it as it would a real file (its tab shown,
    /// its drop area lit, its caption); dropped there, the step is tried, and nothing is sent (see OnDrop).
    /// </summary>
    private void DragTourSample(Microsoft.UI.Xaml.DragEventArgs e)
    {
        var zone = _dropTargets.FirstOrDefault();
        if (zone is null || !_layout.BoundsOf(OpenNow).Contains(e.GetPosition(Root)))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            if (_drag is not null)
            {
                EndDrag(); // Back over its card: not dropped yet.
            }

            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        if (_drag is null)
        {
            _drag = zone;
            _dragHold = _controller.Hold();
            SelectTab(zone.Module);
            zone.Target.OnDragHover(true);
        }

        e.DragUIOverride.Caption = zone.Target.DropCaption;
        if (_modules[zone.Module].View is { } view)
        {
            zone.Target.OnDragMoved(e.GetPosition(view));
        }
    }

    /// <summary>
    /// The tour stops (its card goes back into the notch, unless the notch closing takes it: <paramref name="drawBack"/>
    /// false; the ring fades); the notch closes once left.
    /// </summary>
    private void EndTour(bool drawBack = true)
    {
        if (_tour is not { } tour)
        {
            return;
        }

        _tour = null;
        _tourTimer?.Stop();
        tour.Watching?.Dispose();
        _ringShown?.AnimateTo(0, Motion.Fade);
        if (drawBack && _bubble is not null && _bubble == tour.Bubble)
        {
            tour.Bubble.Dispose();
        }

        tour.Hold?.Dispose();
        UpdateSideBounds();
    }

    private void Step(Tour tour, int by)
    {
        if (_tour != tour)
        {
            return;
        }

        int next = tour.Index + by;
        if (next >= tour.Steps.Count)
        {
            EndTour(); // Done.
            return;
        }

        _tourTimer?.Stop();
        tour.Watching?.Dispose();
        tour.Index = Math.Max(0, next);
        tour.Done = false;
        var step = tour.Steps[tour.Index];
        ShowStepModule(step);
        tour.Bubble?.Resize(tour.Card.Show(step, tour.Index, tour.Steps.Count));
        Watch(tour);
        RingRound(step.Target, appear: false);
    }

    /// <summary>A step about a module (the Révisions, the player): the notch shows its tab meanwhile.</summary>
    private void ShowStepModule(TourStep step)
    {
        if (step.Module is { } type && _modules.FindIndex(slot => slot.Module.GetType() == type && slot.Module.HasTab) is >= 0 and int index)
        {
            SelectTab(index);
        }
    }

    /// <summary>A step tried by a shortcut, or the like: its own watch, for as long as it is shown.</summary>
    private void Watch(Tour tour)
    {
        var step = tour.Steps[tour.Index];
        tour.Watching = step.Wait == TourWait.Custom && step.Await is { } watch
            ? watch(() => TourDid(TourWait.Custom))
            : null;
    }

    /// <summary>
    /// The user just did <paramref name="what"/>: if the step shown waits for it, it is tried. Returns <c>true</c> when the
    /// tour took it (the gesture does nothing else then: the mascot clicked does not open the assistant).
    /// </summary>
    private bool TourDid(TourWait what)
    {
        if (_tour is not { } tour || tour.Done || tour.Steps[tour.Index].Wait != what)
        {
            return false;
        }

        tour.Done = true;
        tour.Watching?.Dispose();
        tour.Watching = null;
        if (tour.Steps[tour.Index].StepsAside)
        {
            StepAside(tour, advance: true);
            return true;
        }

        tour.Bubble?.Resize(tour.Card.Show(tour.Steps[tour.Index], tour.Index, tour.Steps.Count, done: true));
        Buddy.Exclaim(); // Pleased.
        _motion.Width.Kick(160, Motion.Bump); // The notch nods.

        int index = tour.Index;
        _tourTimer ??= DispatcherQueue.CreateTimer();
        _tourTimer.Stop();
        _tourTimer.Interval = TourDoneFor;
        _tourTimer.IsRepeating = false;
        _tourTimer.Tick += OnDone;
        _tourTimer.Start();
        return true;

        void OnDone(DispatcherQueueTimer timer, object args)
        {
            timer.Tick -= OnDone;
            if (_tour == tour && tour.Index == index)
            {
                Step(tour, +1);
            }
        }
    }

    /// <summary>
    /// The step tried opens something that takes the notch (the search), or a shortcut does meanwhile (Ctrl + Q at any
    /// step): the card goes back in, the ring fades, the notch is let go; the tour comes back once that is closed (see
    /// <see cref="ResumeTour"/>), at the next step when this one was tried (<paramref name="advance"/>), else at this one.
    /// </summary>
    private void StepAside(Tour tour, bool advance)
    {
        tour.Away = true;
        tour.Advance = advance;
        tour.Watching?.Dispose(); // Its watch (a shortcut captured) lets go meanwhile; it starts again with the card.
        tour.Watching = null;
        _tourTimer?.Stop();
        _ringShown?.AnimateTo(0, Motion.Fade);
        if (_bubble is not null && _bubble == tour.Bubble)
        {
            tour.Bubble.Dispose();
        }

        tour.Bubble = null;
        tour.Hold?.Dispose();
        tour.Hold = null;
    }

    /// <summary>The tour's sample file dragged over the notch (see <see cref="TourCard.SampleProperty"/>).</summary>
    private bool IsTourSample(DataPackageView data) => _tour is not null && data.Properties.ContainsKey(TourCard.SampleProperty);

    /// <summary>Whether this notch shows that part now (open on a tab).</summary>
    private bool HasTarget(TourTarget target) => target switch
    {
        TourTarget.Quick => _quick is not null && _tabState is not null,
        TourTarget.Mascot => MascotOn,
        TourTarget.Tabs => _tabState is not null,
        _ => true,
    };

    /// <summary>The ring goes round <paramref name="target"/> (springing there; or there at once as it appears).</summary>
    private void RingRound(TourTarget target, bool appear)
    {
        EnsureRing();
        if (!HasTarget(target))
        {
            target = TourTarget.Notch;
        }

        if (target == TourTarget.None)
        {
            _ringShown!.AnimateTo(0, Motion.Fade);
            return;
        }

        var (bounds, radius) = TargetBounds(target);
        bounds = Inflate(bounds, RingMargin);
        radius += RingMargin;

        // Hanging from the screen's edge (the notch, its little notches): the ring goes up past the edge, its top corners
        // out of sight, so it hugs it like a U rather than closing over it.
        if (_layout.IsFlush && bounds.Top <= _layout.TopInset)
        {
            double above = radius + RingGlowThickness;
            bounds = new Rect(bounds.X, bounds.Y - above, bounds.Width, bounds.Height + above);
        }

        var tint = MascotPalette.Find(_settings.MascotColor).Accent;
        _ringStroke!.Color = Color.FromArgb(0xF0, tint.R, tint.G, tint.B);
        _ringGlow!.Color = Color.FromArgb(0x38, tint.R, tint.G, tint.B);

        bool snap = appear || _ringShown!.ValueAgo(TimeSpan.Zero) < 0.05;
        Move(_ringX!, bounds.X);
        Move(_ringY!, bounds.Y);
        Move(_ringWidth!, bounds.Width);
        Move(_ringHeight!, bounds.Height);
        Move(_ringRadius!, radius);
        _ringShown!.AnimateTo(1, Motion.Fade);

        void Move(SpringChannel channel, double to)
        {
            if (snap)
            {
                channel.Snap(to);
            }
            else
            {
                channel.AnimateTo(to, Motion.Resize);
            }
        }
    }

    /// <summary>
    /// Where <paramref name="target"/> is drawn in the open notch (the window's coordinates, its body only: not the
    /// shoulders), and its corners.
    /// </summary>
    private (Rect Bounds, double Radius) TargetBounds(TourTarget target)
    {
        var open = OpenNow;
        double notchLeft = (_layout.CanvasWidth - open.Width) / 2;
        switch (target)
        {
            case TourTarget.Tabs:
                double tabsLeft = (_layout.CanvasWidth - _layout.Expanded.Width) / 2 + TabInset - (Math.Max(open.Width, _layout.Expanded.Width) - _layout.Expanded.Width) / 2;
                return (new Rect(tabsLeft, _layout.TopInset + TabTop, TabBar.Width, TabHeight), TabHeight / 2);

            case TourTarget.Quick:
                double quickLeft = notchLeft - _layout.PodGap - _layout.QuickSize;
                double quickHeight = _quick!.Brightness.IsAvailable ? 2 * _layout.QuickSize + _layout.QuickGap : _layout.QuickSize;
                return (new Rect(quickLeft, _layout.TopInset, _layout.QuickSize, quickHeight), _layout.QuickSize / 2);

            case TourTarget.Mascot:
                double podLeft = notchLeft + open.Width + _layout.PodGap;
                return (new Rect(podLeft, _layout.TopInset, _layout.PodSize, _layout.PodSize), _layout.PodSize / 2);

            default:
                return (new Rect(notchLeft, _layout.TopInset, open.Width, open.Height), open.Radius);
        }
    }

    /// <summary>The ring, made the first time: a rounded rectangle's outline and a softer, wider one under it.</summary>
    private void EnsureRing()
    {
        if (_ring is not null)
        {
            return;
        }

        var compositor = _motion.Compositor;
        _ring = compositor.CreatePropertySet();
        _ringX = new SpringChannel(_ring, "X", 0);
        _ringY = new SpringChannel(_ring, "Y", 0);
        _ringWidth = new SpringChannel(_ring, "W", 0);
        _ringHeight = new SpringChannel(_ring, "H", 0);
        _ringRadius = new SpringChannel(_ring, "R", 0);
        _ringShown = new SpringChannel(_ring, "Shown", 0);

        var geometry = compositor.CreateRoundedRectangleGeometry();
        _motion.Bind("Vector2(r.X, r.Y)").With("r", _ring).Start(geometry, "Offset");
        _motion.Bind("Vector2(Max(r.W, 0), Max(r.H, 0))").With("r", _ring).Start(geometry, "Size");
        _motion.Bind("Vector2(Max(r.R, 0), Max(r.R, 0))").With("r", _ring).Start(geometry, "CornerRadius");

        _ringGlow = compositor.CreateColorBrush(Colors.Transparent);
        _ringStroke = compositor.CreateColorBrush(Colors.Transparent);
        var glow = compositor.CreateSpriteShape(geometry);
        glow.StrokeBrush = _ringGlow;
        glow.StrokeThickness = RingGlowThickness;
        var line = compositor.CreateSpriteShape(geometry);
        line.StrokeBrush = _ringStroke;
        line.StrokeThickness = RingThickness;

        var visual = compositor.CreateShapeVisual();
        visual.Size = new Vector2((float)_layout.CanvasWidth, (float)_layout.CanvasHeight);
        visual.Shapes.Add(glow);
        visual.Shapes.Add(line);
        _motion.Bind("Clamp(r.Shown, 0, 1)").With("r", _ring).Start(visual, "Opacity");
        ElementCompositionPreview.SetElementChildVisual(TourLayer, visual);
    }

    private sealed class Tour(IReadOnlyList<TourStep> steps, TourCard card, IDisposable hold)
    {
        public IReadOnlyList<TourStep> Steps { get; } = steps;

        public TourCard Card { get; } = card;

        /// <summary>The notch kept open; none while the tour stepped aside.</summary>
        public IDisposable? Hold { get; set; } = hold;

        /// <summary>
        /// Stepped aside for what its step opened (see <see cref="TourStep.StepsAside"/>), or for what a shortcut opened
        /// meanwhile (the search, the assistant).
        /// </summary>
        public bool Away { get; set; }

        /// <summary>Back from aside, it goes on at the next step (its step was tried); else at the same one.</summary>
        public bool Advance { get; set; }

        public NotchBubble? Bubble { get; set; }

        public int Index { get; set; }

        /// <summary>The step shown was tried: it says so, and goes on in a moment.</summary>
        public bool Done { get; set; }

        /// <summary>The step's own watch (a shortcut…), while it is shown.</summary>
        public IDisposable? Watching { get; set; }
    }
}
