using Avalonia.Controls;

namespace Perch.Strings;

// One class per language file. Compiling each file to its own class (instead of loading it by URI at run time)
// keeps the language switch free of reflection, which Native AOT needs.
public partial class EnglishStrings : ResourceDictionary
{
    public EnglishStrings() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}

public partial class ChineseStrings : ResourceDictionary
{
    public ChineseStrings() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
