using System.Windows;
using Wpf.Ui.Controls;

namespace Voca;

/// <summary>
/// A Fluent icon in front of a button's (or tab's) text: <c>local:UiIcon.Symbol="Add24"</c>. The button
/// template in App.xaml draws it, so coloured buttons keep their own look instead of WPF-UI's.
/// </summary>
public static class UiIcon
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.RegisterAttached(
        "Symbol", typeof(SymbolRegular), typeof(UiIcon), new FrameworkPropertyMetadata(SymbolRegular.Empty));

    public static SymbolRegular GetSymbol(DependencyObject element) => (SymbolRegular)element.GetValue(SymbolProperty);

    public static void SetSymbol(DependencyObject element, SymbolRegular value) => element.SetValue(SymbolProperty, value);
}
