using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DepupCode.Services;

/// <summary>Disables controls that require a network connection and explains why they are unavailable.</summary>
public static class CloudControlGate
{
    private sealed class GateState
    {
        public bool IsGated { get; set; }
        public bool WasEnabled { get; set; }
        public object? PreviousToolTip { get; set; }
    }

    private static readonly ConditionalWeakTable<Button, GateState> States = new();
    private static bool _canUseCloud;
    private static string _reason = "Depup Cloud belum tersambung.";

    static CloudControlGate()
    {
        EventManager.RegisterClassHandler(typeof(Button), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnButtonLoaded), true);
    }

    public static void SetAvailability(DependencyObject? root, bool canUseCloud, string reason)
    {
        _canUseCloud = canUseCloud;
        _reason = reason;
        if (root is not null) ApplyRecursive(root, new HashSet<DependencyObject>());
    }

    public static void Refresh(DependencyObject root) => ApplyRecursive(root, new HashSet<DependencyObject>());

    private static void OnButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) ApplyButton(button);
    }

    private static void ApplyRecursive(DependencyObject node, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(node)) return;
        if (node is Button button) ApplyButton(button);

        if (node is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
                ApplyRecursive(VisualTreeHelper.GetChild(node, i), visited);
        }

        if (node is FrameworkElement element)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
                ApplyRecursive(child, visited);
        }
    }

    private static void ApplyButton(Button button)
    {
        if (!string.Equals(button.Tag as string, "RequiresCloud", StringComparison.Ordinal) &&
            !string.Equals(System.Windows.Automation.AutomationProperties.GetHelpText(button), "RequiresCloud", StringComparison.Ordinal)) return;
        var state = States.GetOrCreateValue(button);
        if (!_canUseCloud)
        {
            if (!state.IsGated)
            {
                state.WasEnabled = button.IsEnabled;
                state.PreviousToolTip = button.ToolTip;
                state.IsGated = true;
            }
            else if (button.IsEnabled)
            {
                // Preserve enabled state changes made by the owning view while it was gated.
                state.WasEnabled = true;
            }
            button.IsEnabled = false;
            button.ToolTip = _reason;
            ToolTipService.SetShowOnDisabled(button, true);
            return;
        }

        if (!state.IsGated) return;
        if (button.IsEnabled) state.WasEnabled = true;
        button.IsEnabled = state.WasEnabled;
        button.ToolTip = state.PreviousToolTip;
        state.IsGated = false;
    }
}
