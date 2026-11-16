using Avalonia.Controls;
using Perch.ViewModels;

namespace Perch.Views.Pages;

public partial class PortsPage : UserControl
{
    public PortsPage() => InitializeComponent();
    private PortsViewModel Model => (PortsViewModel)DataContext!;

    public void AddRule(PortEntry? port) => Model.OpenRuleEditor(port);
    public void ConfirmEndProcess(PortEntry port) => Model.ConfirmEndProcess(port);
}
