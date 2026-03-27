using System.Windows;

namespace MDViewer;

public partial class ExportDialog : Window
{
    public bool IsPdfFormat => FormatPdf.IsChecked == true;
    public bool IsHtmlFormat => FormatHtml.IsChecked == true;
    public bool IncludeLinkedDocuments => IncludeLinkedDocsCheckBox.IsChecked == true;

    public ExportDialog(int linkedDocCount)
    {
        InitializeComponent();

        if (linkedDocCount > 0)
        {
            IncludeLinkedDocsCheckBox.Content = $"Include linked documents ({linkedDocCount} found)";
            LinkedDocsInfo.Text = "All linked Markdown files will be merged into the export.";
        }
        else
        {
            IncludeLinkedDocsCheckBox.Content = "Include linked documents (0 found)";
            IncludeLinkedDocsCheckBox.IsChecked = false;
            IncludeLinkedDocsCheckBox.IsEnabled = false;
            LinkedDocsInfo.Text = "No linked Markdown files were found in this document.";
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
