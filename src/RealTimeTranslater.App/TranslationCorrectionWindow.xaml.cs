using System.Windows;
using System.Windows.Controls;
using RealTimeTranslater.Core.Models;

namespace RealTimeTranslater.App;

public partial class TranslationCorrectionWindow : Window
{
    private readonly IReadOnlyList<TranslatedRegion> _entries;

    public TranslationCorrectionWindow(
        IReadOnlyList<TranslatedRegion> entries)
    {
        InitializeComponent();

        _entries =
            entries
                .Where(entry =>
                    !string.IsNullOrWhiteSpace(
                        entry.OriginalText))
                .ToArray();

        EntryComboBox.ItemsSource =
            _entries
                .Select((entry, index) =>
                    new EntryOption(
                        index,
                        BuildLabel(entry)))
                .ToArray();

        if (_entries.Count > 0)
            EntryComboBox.SelectedIndex = 0;
    }

    public string SelectedSourceText { get; private set; } =
        string.Empty;

    public string CorrectedTranslation { get; private set; } =
        string.Empty;

    private void EntryComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (EntryComboBox.SelectedItem is not
            EntryOption option ||
            option.Index < 0 ||
            option.Index >= _entries.Count)
        {
            return;
        }

        var entry =
            _entries[option.Index];

        SourceTextBox.Text =
            entry.OriginalText;

        TranslationTextBox.Text =
            entry.TranslatedText;

        TranslationTextBox.SelectAll();
        TranslationTextBox.Focus();
    }

    private void SaveButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (EntryComboBox.SelectedItem is not
            EntryOption option ||
            option.Index < 0 ||
            option.Index >= _entries.Count)
        {
            return;
        }

        var corrected =
            TranslationTextBox.Text.Trim();

        if (corrected.Length == 0)
        {
            MessageBox.Show(
                this,
                "Translation cannot be empty.",
                "RealTime Translater",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SelectedSourceText =
            _entries[option.Index]
                .OriginalText;

        CorrectedTranslation =
            corrected;

        DialogResult = true;
    }

    private static string BuildLabel(
        TranslatedRegion entry)
    {
        var source =
            string.Join(
                " ",
                entry.OriginalText.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries));

        if (source.Length > 72)
            source = source[..72] + "…";

        return source;
    }

    private sealed record EntryOption(
        int Index,
        string Label)
    {
        public override string ToString()
            => Label;
    }
}
