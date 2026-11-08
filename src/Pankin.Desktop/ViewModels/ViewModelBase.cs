using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Pankin.Desktop.Services;

namespace Pankin.Desktop.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    public CornerRadius PanelCornerRadius => new(DesktopAppContext.Instance.SettingsStore.Current.General.PanelRadius);

    protected ViewModelBase()
    {
        DesktopAppContext.Instance.SettingsStore.Changed += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(PanelCornerRadius)));
        };
    }
}
