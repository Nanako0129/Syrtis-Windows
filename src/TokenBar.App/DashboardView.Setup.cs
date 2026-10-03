using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TokenBar.Core;
using Windows.UI;

namespace TokenBar.App;

/// <summary>
/// The first-run setup cards at the top of the global Overview (macOS
/// <c>OnboardingSetupCards.swift</c>, <c>AnimationPaceOnboardingCard.swift</c>).
/// Every decision (which card shows, what answering writes, the count, the
/// copy) lives in <see cref="OnboardingSetup"/>; this file only draws and
/// wires clicks, because no test project compiles it.
/// </summary>
public sealed partial class DashboardView
{
    private bool _setupLoginFailed;

    private List<UIElement> BuildSetupCards(DashboardModel.Snapshot snapshot)
    {
        var cards = new List<UIElement>();
        var store = AppSettings.Store;
        var args = Environment.GetCommandLineArgs();
        // Windows autostart is a registry Run value, writable from any build:
        // there is no bare-executable case like macOS's `make run`.
        const bool loginAvailable = true;

        var attribution = BuildAttributionOnboardingCard(snapshot);
        var paceShows = OnboardingSetup.PaceCardShows(store, args);
        var left = OnboardingSetup.Remaining(store, loginAvailable, paceShows, attribution is not null);

        if (OnboardingSetup.HeaderVisible(args, left))
        {
            cards.Add(BuildSetupHeader(left));
        }

        bool Shows(OnboardingStep step) => OnboardingSetup.Shows(store, step, args, loginAvailable);

        if (Shows(OnboardingStep.Agents))
        {
            cards.Add(BuildSetupAgentsCard(snapshot));
        }

        if (Shows(OnboardingStep.Icon))
        {
            cards.Add(BuildSetupIconCard());
        }

        if (Shows(OnboardingStep.Title))
        {
            cards.Add(BuildSetupTitleCard());
        }

        if (paceShows)
        {
            cards.Add(BuildSetupPaceCard());
        }

        if (attribution is not null)
        {
            cards.Add(attribution);
        }

        if (Shows(OnboardingStep.Login))
        {
            cards.Add(BuildSetupLoginCard());
        }

        if (Shows(OnboardingStep.Discord))
        {
            cards.Add(BuildSetupDiscordCard());
        }

        return cards;
    }

    private void AnswerSetup(OnboardingStep step)
    {
        OnboardingSetup.Answer(AppSettings.Store, step);
        RenderContent(animated: false);
    }

    private static Button SetupButton(string text, bool accent, Action click)
    {
        var button = new Button { Content = text.Localized(), FontSize = 12 };
        if (accent)
        {
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        }

        button.Click += (_, _) => click();
        return button;
    }

    private static StackPanel SetupButtonRow(params Button[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        foreach (var button in buttons)
        {
            row.Children.Add(button);
        }

        return row;
    }

    private static TextBlock SetupBody(string text) => Ui.Dim(text.Localized(), 11);

    private FrameworkElement BuildSetupHeader(int left)
    {
        var skip = SetupButton(OnboardingSetup.Copy.SkipAll, accent: false, () =>
        {
            OnboardingSetup.SkipAll(AppSettings.Store);
            RenderContent(animated: false);
        });
        return Ui.Card(
            OnboardingSetup.Copy.HeaderTitle.Localized(),
            Ui.Dim(OnboardingSetup.Copy.HeaderRemaining.Localized(left), 11),
            trailing: skip);
    }

    private FrameworkElement BuildSetupAgentsCard(DashboardModel.Snapshot snapshot)
    {
        var present = OnboardingSetup.PresentTabClients(
            snapshot.Graph.Summary.Clients, snapshot.Quota?.ConfiguredClientIds ?? []);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(SetupBody(OnboardingSetup.Copy.AgentsBody));
        body.Children.Add(present.Count == 0
            ? Ui.Dim(OnboardingSetup.Copy.AgentsNone.Localized(), 10)
            : Ui.Text(string.Join(" · ", present.Select(ClientRegistry.TabDisplayName)), 11));
        body.Children.Add(SetupButtonRow(
            SetupButton(OnboardingSetup.Copy.ChooseTabs, accent: false, () =>
            {
                TrayService.OpenDashboardSettings?.Invoke();
                AnswerSetup(OnboardingStep.Agents);
            }),
            SetupButton(OnboardingSetup.Copy.LooksGood, accent: true,
                () => AnswerSetup(OnboardingStep.Agents))));
        return Ui.Card(OnboardingSetup.Copy.AgentsTitle.Localized(), body);
    }

    // Picking applies at once so the live icon can be tried; Done answers.
    private FrameworkElement BuildSetupIconCard()
    {
        var store = AppSettings.Store;
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(SetupBody(OnboardingSetup.Copy.IconBody));
        body.Children.Add(SetupChoices(
            [.. TrayIconStyles.Options],
            store.GetString("tokenbar.tray.animationStyle", "cat") ?? "cat",
            raw => store.SetString("tokenbar.tray.animationStyle", raw)));
        body.Children.Add(SetupButtonRow(SetupButton(
            OnboardingSetup.Copy.Done, accent: true, () => AnswerSetup(OnboardingStep.Icon))));
        return Ui.Card(OnboardingSetup.Copy.IconTitle.Localized(), body);
    }

    private FrameworkElement BuildSetupTitleCard()
    {
        var store = AppSettings.Store;
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(SetupBody(OnboardingSetup.Copy.TitleBody));
        body.Children.Add(SetupChoices(
            [.. TrayModes.All.Select(m => (m.RawValue(), m.Label()))],
            TrayModes.Parse(store.GetString(TrayModes.StorageKey)).RawValue(),
            raw => store.SetString(TrayModes.StorageKey, raw)));
        body.Children.Add(SetupButtonRow(SetupButton(
            OnboardingSetup.Copy.Done, accent: true, () => AnswerSetup(OnboardingStep.Title))));
        return Ui.Card(OnboardingSetup.Copy.TitleTitle.Localized(), body);
    }

    /// <summary>Two-column wash grid (OnboardingSetupCards.swift:259-283): the
    /// picked option is drawn stronger.</summary>
    private FrameworkElement SetupChoices(
        IReadOnlyList<(string Value, string Label)> options, string selected, Action<string> pick)
    {
        var accent = AccentColor();
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < options.Count; i++)
        {
            var (value, label) = options[i];
            var picked = value == selected;
            var button = new Button
            {
                Content = Ui.Text(label, 11),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Tint(accent,
                    picked ? OnboardingSetup.ChoiceSelectedFill : OnboardingSetup.ChoiceFill)),
                BorderBrush = new SolidColorBrush(Tint(accent,
                    picked ? OnboardingSetup.ChoiceSelectedStroke : OnboardingSetup.ChoiceStroke)),
            };
            button.Click += (_, _) =>
            {
                pick(value);
                RenderContent(animated: false);
            };
            if (grid.RowDefinitions.Count <= i / 2)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            Grid.SetRow(button, i / 2);
            Grid.SetColumn(button, i % 2);
            grid.Children.Add(button);
        }

        return grid;
    }

    private FrameworkElement BuildSetupPaceCard()
    {
        var store = AppSettings.Store;
        var current = AnimationPaces.Current(store);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(SetupBody(OnboardingSetup.Copy.PaceBody));
        foreach (var pace in AnimationPaces.All)
        {
            var (top, bottom) = OnboardingSetup.PaceTint(pace);
            var topColor = Color.FromArgb(255, top.R, top.G, top.B);
            var bottomColor = Color.FromArgb(255, bottom.R, bottom.G, bottom.B);
            var picked = pace == current;
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(Ui.Text(pace.Label(), 11, bold: true));
            text.Children.Add(Ui.Dim(pace.Detail(), 10));
            var option = new Button
            {
                Content = text,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Tint(bottomColor,
                    picked ? OnboardingSetup.PaceSelectedFill : OnboardingSetup.PaceOptionFill)),
                BorderBrush = new SolidColorBrush(Tint(topColor,
                    picked ? OnboardingSetup.PaceSelectedStroke : OnboardingSetup.PaceOptionStroke)),
                BorderThickness = new Thickness(picked ? 1.5 : 1),
            };
            option.Click += (_, _) =>
            {
                store.SetString(AnimationPaces.StorageKey, pace.RawValue());
                RenderContent(animated: false);
            };
            body.Children.Add(option);
        }

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(Ui.Dim(OnboardingSetup.Copy.PaceRecommended.Localized(), 10));
        var done = SetupButton(OnboardingSetup.Copy.Done, accent: true, () =>
        {
            // AnimationPaceOnboardingCardView "Done": settle an unset pace on
            // the default, then answer.
            if (!OnboardingSetup.IsPaceChosen(store.GetString(AnimationPaces.StorageKey)))
            {
                store.SetString(AnimationPaces.StorageKey, AnimationPaces.Default.RawValue());
            }

            store.SetBool(OnboardingSetup.PaceAnsweredKey, true);
            RenderContent(animated: false);
        });
        Grid.SetColumn(done, 1);
        footer.Children.Add(done);
        body.Children.Add(footer);
        return Ui.Card(OnboardingSetup.Copy.PaceTitle.Localized(), body);
    }

    private FrameworkElement BuildSetupLoginCard()
    {
        var enabled = AutostartService.IsEnabled;
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(SetupBody(enabled
            ? OnboardingSetup.Copy.LoginAlreadyOn : OnboardingSetup.Copy.LoginBody));
        if (_setupLoginFailed)
        {
            var failed = Ui.Dim(OnboardingSetup.Copy.LoginFailed.Localized(), 10);
            failed.Opacity = 1;
            failed.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            body.Children.Add(failed);
        }

        if (enabled)
        {
            body.Children.Add(SetupButtonRow(SetupButton(
                OnboardingSetup.Copy.Done, accent: true, () => AnswerSetup(OnboardingStep.Login))));
        }
        else
        {
            body.Children.Add(SetupButtonRow(
                SetupButton(OnboardingSetup.Copy.LoginOff, accent: false,
                    () => AnswerSetup(OnboardingStep.Login)),
                SetupButton(OnboardingSetup.Copy.LoginOn, accent: true, () =>
                {
                    // Answered only when it took: a failed write keeps the
                    // card, with the reason (OnboardingSetupCards.swift:322-329).
                    if (AutostartService.SetEnabled(true))
                    {
                        _setupLoginFailed = false;
                        AnswerSetup(OnboardingStep.Login);
                    }
                    else
                    {
                        _setupLoginFailed = true;
                        RenderContent(animated: false);
                    }
                })));
        }

        return Ui.Card(OnboardingSetup.Copy.LoginTitle.Localized(), body);
    }

    // Two buttons of equal weight, neither accent: a prominent "set up" next
    // to a plain "no" is a thumb on the scale, and this card only points at
    // the Settings disclosure (OnboardingSetupCards.swift:222-227).
    private FrameworkElement BuildSetupDiscordCard()
    {
        var store = AppSettings.Store;
        var alreadyOn = OnboardingSetup.DiscordAlreadyOn(store);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(SetupBody(alreadyOn
            ? OnboardingSetup.Copy.DiscordAlreadyOn : OnboardingSetup.Copy.DiscordBody));
        void Choose(DiscordChoice choice, Action openSettings)
        {
            OnboardingSetup.Perform(store, choice, openSettings);
            RenderContent(animated: false);
        }

        body.Children.Add(alreadyOn
            ? SetupButtonRow(SetupButton(OnboardingSetup.Copy.Done, accent: false,
                () => Choose(DiscordChoice.NotNow, () => { })))
            : SetupButtonRow(
                SetupButton(OnboardingSetup.Copy.DiscordNo, accent: false,
                    () => Choose(DiscordChoice.NotNow, () => { })),
                SetupButton(OnboardingSetup.Copy.DiscordSetUp, accent: false,
                    () => Choose(DiscordChoice.SetUp,
                        () => TrayService.OpenDiscordSettings?.Invoke()))));
        return Ui.Card(OnboardingSetup.Copy.DiscordTitle.Localized(), body);
    }
}
